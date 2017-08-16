using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace Romulus.Patch
{
    public static class IPS
    {
        public static byte[] CreateIPS(byte[] oldRom, byte[] newRom) {
            return CreateIPS(oldRom, newRom, false);
        }
        public static byte[] CreateIPS(byte[] oldRom, byte[] newRom, bool truncate) {
            if (oldRom == null) throw new ArgumentNullException("oldRom");
            if (newRom == null) throw new ArgumentNullException("newRom");

            IpsMaker maker = new IpsMaker(oldRom, newRom);
            return maker.MakeIPS();
        }

        struct RecordInfo
        {
            public readonly bool IsRLE;
            public readonly int offset;
            public readonly int length;

            public RecordInfo(int offset, int length, bool rle) {
                this.IsRLE = rle;
                this.offset = offset;
                this.length = length;
            }

            public RecordInfo AsRLE() {
                return new RecordInfo(offset, length, true);
            }
        }

        public class IpsMakerBase
        {
            protected const int recordHeaderSize = 5;
            protected const int rleHeaderSize = 7;
            protected const int maxRecordSize = ushort.MaxValue;
            protected static readonly byte[] PATCH_bytes = Encoding.ASCII.GetBytes("PATCH");
            protected static readonly byte[] EOF_bytes = Encoding.ASCII.GetBytes("EOF");
            protected const int eofCollisionOffset = 0x454F46;

        }
        /// <summary>
        /// Creates an IPS file from "before" and "after" files.
        /// </summary>
        private class IpsMaker:IpsMakerBase
        {
            List<RecordInfo> records = new List<RecordInfo>();
            byte[] source, dest;


            /// <summary>
            /// If true, a "truncate" offset will be specified. Patchers that support truncate will truncate a ROM to the correct size after patching.
            /// </summary>
            public bool Truncate { get; set; }

            /// <summary>
            /// Returns the IPS file as a byte array after MakeIPS is called.
            /// </summary>
            public byte[] IpsFile { get; private set; }

            public IpsMaker(byte[] sourceRom, byte[] resultRom) {
                if (sourceRom == null) throw new ArgumentNullException("sourceRom");
                if (resultRom == null) throw new ArgumentNullException("resultRom");
                
                this.source = sourceRom;
                this.dest = resultRom;
            }

            /// <summary>
            /// Examines the original and new versions of the ROM and produces an IPS file.
            /// </summary>
            /// <returns>A byte array containing the IPS file contents.</returns>
            public byte[] MakeIPS() {
                FindDiffs();
                FindRLEs();
                SplitBigRecords();
                SortRecords();
                CreateIPS();

                return IpsFile;
            }





            /// <summary>
            /// Identifies and tags any records that, in-part or whole, can be encoded as RLE to reduce file size.
            /// </summary>
            private void FindRLEs() {
                FindWholeRLEs();
                FindSubRLEs();

            }

            /// <summary>
            /// Creates a RecordInfo object for each difference in the two ROMs.
            /// </summary>
            private void FindDiffs() {
                int i = 0;
                int term = Math.Min(source.Length, dest.Length);
                while (i < term) {
                    if (source[i] == dest[i]) { 
                        // Matching bytes, skip over
                        i++;
                    } else {
                        records.Add(GetRecordAt(ref i));
                    }
                }

                // If dest is bigger, all the extra data will be stored in a record.
                if (dest.Length > source.Length) {
                    records.Add(new RecordInfo(source.Length,dest.Length - source.Length,false));
                }
            }

            /// <summary>
            /// Returns a RecordInfo identifying the location and size of a difference
            /// at the specifie location, and updates the 'offset' parameter to point to
            /// the first byte after the difference.
            /// </summary>
            /// <param name="offset">Identifies the offset to look for a difference, and returns the first byte after the difference.</param>
            /// <returns>A RecordInfo identifying the location and size of the difference. The size will be zero of there is no difference.</returns>
            private RecordInfo GetRecordAt(ref int offset) {
                int start = offset;
                int term = Math.Min(source.Length, dest.Length);

                if (offset < 0 || offset >= term) throw new ArgumentException("Offset is out of range.", "offset");
                if (source[offset] == dest[offset]) return new RecordInfo(offset, 0, false);

                int i = offset + 1; // index of byte we're currently examining
                int endOfDiff = offset + 1; // index of the end of the diff

                while (true) {
                    // Scan until we find non-different bytes
                    while (source[i] != dest[i]) {
                        i++;

                        // If we hit the end of data, return now
                        if (i == term) {
                            offset = i;
                            return new RecordInfo(start, i - start, false);
                        }
                    }
                    endOfDiff = i;

                    // Find end of matching bytes (i currently points to matching bytes). 
                    int sizeOfNonDiff = 0;
                    while (source[i] == dest[i]) {
                        i++;
                        sizeOfNonDiff++;

                        // If we hit the end of data, or there are too many matching bytes, return now
                        if (i == term || sizeOfNonDiff == recordHeaderSize) {
                            offset = endOfDiff;
                            return new RecordInfo(start, endOfDiff - start, false);
                        }
                    }
                    
                    // If we got here, there were few enough matching bytes that we'll just include them 
                    // in the same record to save the overhead of an extra record header.
                }
            }

            /// <summary>
            /// Finds RLE-encodable data in non-RLE records, and divides the records into smaller
            /// RLE and non-RLE records.
            /// </summary>
            private void FindSubRLEs() {
                // Record count is cached because as we find RLE-encodable pieces of records, they are appended
                // as new records. There is no point in checking them.
                int recordCount = records.Count;

                for (int iRecord = 0; iRecord < recordCount; iRecord++) {
                    if(!records[iRecord].IsRLE) // Skip already-RLE records
                        TryGetSubRles(iRecord);
                }
            }

            /// <summary>
            /// Finds RLE-encodable data in a single non-RLE record, and divides the record into smaller
            /// RLE and non-RLE records.
            /// /// </summary>
            /// <param name="iRecord"></param>
            private void TryGetSubRles(int iRecord) {
                var record = records[iRecord];

                int lastByte = dest[record.offset];
                int runlength = 1;
                int runstart = record.offset;

                int term = record.offset + record.length;
                for (int i = record.offset + 1; i < term; i++) {
                    int thisByte = dest[i];
                    if (thisByte == lastByte)
                        runlength++;
                    else {
                        if (runlength > rleHeaderSize) { // Will an RLE save memory?
                            bool isAtStart = (runstart == record.offset);

                            int threshold = isAtStart ? rleHeaderSize : (rleHeaderSize + recordHeaderSize);
                            if (runlength > threshold) {
                                // Split the record into three records: pre-run, run, and after-run. Zero-length records are allowed.
                                RecordInfo beforeRLE = new RecordInfo(record.offset, runstart - record.offset, false);
                                RecordInfo rleRecord = new RecordInfo(runstart, runlength, true);
                                RecordInfo afterRLE = new RecordInfo(runstart + runlength, record.length - beforeRLE.length - rleRecord.length, false);

                                // These records are appended to the record list
                                if(beforeRLE.length > 0)
                                    records.Add(beforeRLE);
                                records.Add(rleRecord);
                                // This record replaces the original
                                records[iRecord] = record = afterRLE;
                            }
                        }

                        runlength = 1;
                        runstart = i;
                        lastByte = thisByte;
                    }
                }

                // We're now at the end of the record.
                if (runlength > rleHeaderSize) {
                    RecordInfo beforeRLE = new RecordInfo(record.offset, record.length - runlength, false);
                    RecordInfo rleRecord = new RecordInfo(runstart, runlength, true);

                    // Append RLE record
                    records.Add(rleRecord);
                    // This record replaces original.
                    records[iRecord] = record = beforeRLE;
                }
            }

            /// <summary>
            /// Identifies any whole records that can be stored as RLE.
            /// </summary>
            private void FindWholeRLEs() {
                for (int iRecord = 0; iRecord < records.Count; iRecord++) {
                    TryMakeRle(iRecord);
                }
            }

            /// <summary>
            /// If the record, as a whole, can be represented as RLE, the record info is updated to reflect this.
            /// </summary>
            /// <param name="iRecord"></param>
            private void TryMakeRle(int iRecord) {
                var record = records[iRecord];
                if (record.length == 1) return; // RLE-ing one byte is silly (and wastes a byte)
                if (record.IsRLE) return; // Already RLE. Don't bother.

                int term = record.offset + record.length;

                byte firstByte = dest[record.offset];
                
                // If all bytes are not the same, return
                for (int i = record.offset + 1; i < term; i++) {
                    if (dest[i] != firstByte) return;
                }

                // All bytes are same, use RLE
                records[iRecord] = record.AsRLE();

            }
            /// <summary>
            /// Divides any records that are too large for the IPS format into multiple smaller records.
            /// </summary>
            private void SplitBigRecords() {
                for (int i = 0; i < records.Count; i++) {
                    // Too big?
                    if (records[i].length > maxRecordSize) {
                        var record = records[i];

                        int remainderStart = record.offset;
                        int remainderLen = record.length;
                        bool isRLE = record.IsRLE;

                        // Replace original oversized record with new max-sized record
                        records[i] = new RecordInfo(record.offset, maxRecordSize, record.IsRLE);
                        remainderStart += maxRecordSize;
                        remainderLen -= maxRecordSize;

                        // Divide remainder into max-sized sections (plus 1 for leftovers)
                        while (remainderLen > 0) {
                            if (remainderLen > maxRecordSize) {
                                // Add another max-sized record
                                records.Add(new RecordInfo(remainderStart, maxRecordSize, isRLE));
                                remainderStart -= maxRecordSize;
                                remainderLen -= maxRecordSize;
                            } else {
                                // Add a record for left-overs
                                records.Add(new RecordInfo(remainderStart, remainderLen, isRLE));
                                remainderLen = 0;
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// Sorts records by offset.
            /// </summary>
            private void SortRecords() {
                records.Sort(Comparer_RecordInfo);
            }

            private int Comparer_RecordInfo(RecordInfo a, RecordInfo b) {
                return a.offset - b.offset;
            }

            private void CreateIPS() {
                MemoryStream output = new MemoryStream();

                // Magic number
                output.Write(PATCH_bytes, 0, PATCH_bytes.Length);

                for (int iRecord = 0; iRecord < records.Count; iRecord++) {
                    var record = records[iRecord];

                    // If a record's offset happens to match the ASCII value for "EOF", it would be mistaken for the end of file.
                    if (record.length > 0) {
                        if (record.offset == eofCollisionOffset) {
                            // So we include an extra byte to avoid that offset
                            record = new RecordInfo(record.offset - 1, record.length + 1, record.IsRLE);
                        }

                        // Big-endian
                        output.WriteByte((byte)(record.offset >> 16));
                        output.WriteByte((byte)(record.offset >> 8));
                        output.WriteByte((byte)(record.offset));

                        if (record.IsRLE) {
                            // A length of zero indicates RLE
                            // (an extra ushort follows to specify actual length)
                            output.WriteByte(0);
                            output.WriteByte(0);
                        }

                        output.WriteByte((byte)(record.length >> 8));
                        output.WriteByte((byte)(record.length));

                        if (record.IsRLE) {
                            output.WriteByte(dest[record.offset]);
                        } else {
                            output.Write(dest, record.offset, record.length);
                        }
                    }
                }

                output.Write(EOF_bytes, 0, EOF_bytes.Length);
                if (Truncate) {
                    int TruncateOffset = dest.Length;
                    
                    output.WriteByte((byte)(TruncateOffset >> 16));
                    output.WriteByte((byte)(TruncateOffset >> 8));
                    output.WriteByte((byte)(TruncateOffset));
                }


                this.IpsFile = output.ToArray();
            }


        }
        private class IpsPatcher
        {
        }

        public class Builder:IpsMakerBase
        {
            struct builderRecord{
                public byte[] data;
                public int srcStart;
                public int destOffset;
                public int length;
                public bool IsRLE;

            }
            List<builderRecord> records = new List<builderRecord>();

            public void AddRecord(byte[] data, int destOffset) {
                AddRecord(data, 0, data.Length, destOffset);
            }

            public void AddRecord(byte[] data, int start, int length, int destOffset) {
                if (destOffset == eofCollisionOffset)
                    throw new ArgumentException("Specified IPS record offset is invalid (it conflicts with EOF marker). Include the previous byte in the record to avoid this issue.");

                builderRecord record;
                record.data = data;
                record.srcStart = start;
                record.length = length;
                record.destOffset = destOffset;
                record.IsRLE = false;

                records.Add(record);
            }
            public void AddRleRecord(byte[] data, int destOffset) {
                AddRleRecord(data, 0, data.Length, destOffset);
            }

            public void AddRleRecord(byte[] data, int start, int length, int destOffset) {
                if (destOffset == eofCollisionOffset)
                    throw new ArgumentException("Specified IPS record offset is invalid (it conflicts with EOF marker). Include the previous byte in the record to avoid this issue.");

                builderRecord record;
                record.data = data;
                record.srcStart = start;
                record.length = length;
                record.destOffset = destOffset;
                record.IsRLE = true;

                records.Add(record);
            }

            public byte[] CreateIPS() {
                MemoryStream output = new MemoryStream();

                // Magic number
                output.Write(PATCH_bytes, 0, PATCH_bytes.Length);

                for (int iRecord = 0; iRecord < records.Count; iRecord++) {
                    var record = records[iRecord];

                    if (record.length > 0) {
                        // Big-endian
                        output.WriteByte((byte)(record.destOffset >> 16));
                        output.WriteByte((byte)(record.destOffset >> 8));
                        output.WriteByte((byte)(record.destOffset));

                        if (record.IsRLE) {
                            // A length of zero indicates RLE
                            // (an extra ushort follows to specify actual length)
                            output.WriteByte(0);
                            output.WriteByte(0);
                        }

                        output.WriteByte((byte)(record.length >> 8));
                        output.WriteByte((byte)(record.length));

                        if (record.IsRLE) {
                            output.WriteByte(record.data[record.srcStart]);
                        } else {
                            output.Write(record.data, record.srcStart, record.length);
                        }
                    }
                }

                output.Write(EOF_bytes, 0, EOF_bytes.Length);
                //if (Truncate) {
                //    int TruncateOffset = dest.Length;

                //    output.WriteByte((byte)(TruncateOffset >> 16));
                //    output.WriteByte((byte)(TruncateOffset >> 8));
                //    output.WriteByte((byte)(TruncateOffset));
                //}


                return output.ToArray();
            }
        }
    }

    
}
