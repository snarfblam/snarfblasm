using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.IO;
using snarfblasm;
using Romulus.Patch;

namespace snarfblasm
{
    static class Program
    {
        static ProgramSwitches switches;
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args) {

            // syntax: (name may change)
            //     snarfblasm sourceFile [destFile] [-o | -os] [-i] [@offset]
            //
            //     sourceFile - Source ASM file
            //     destFile   - Output file. If not specified, the output filename will be sourceFile changed to a .bin extension
            //     @offset    - If specified, part of the file will be overwritten at the specified offset (otherwise the file is replaced). Dec or $Hex.
            // Swithes
            //     -o   Overflow checking
            //     -os  Overflow checking (signed mode)
            //     -i   Allow invalid opcodes
            //     -d   Don't require dot on directive
            if (args.Length == 1 && args[0].ToUpper() == "-DEBUG") {
                args = Console.ReadLine().Split(',');
            }


            if (args.Length == 0|| string.IsNullOrEmpty(args[0].Trim())) {
                ShowHelp();
            } else {
                sourceFile = args[0].Trim();

                for (int i = 1; i < args.Length; i++) {
                    var arg = args[i];
                    bool isFirstArg = i == 1;
                    bool exit;

                    ProcessArg(arg, isFirstArg, out exit);
                    if(exit) return;
                }
                RunAssembler();
            }


#if DEBUG
            Console.ReadLine();
#endif
        }




        private static void RunAssembler() {
            if (!FileReader.IsPseudoFile(sourceFile) && !fileSystem.FileExists(sourceFile)) {
                Console.WriteLine("Error: Source file does not exist.");
                return;
            }

            Assembler asm = new Assembler(Path.GetFileName(sourceFile), fileSystem.GetFileText(sourceFile), fileSystem);
            AddressLabels asmLabels = new AddressLabels();
            asm.Labels = asmLabels;

            // Todo: temp, for testing
            asm.RequireColonOnLabels = false;
            asm.RequireDotOnDirectives = false;

            asm.PhaseComplete += new EventHandler<Assembler.PhaseEventArgs>(asm_PhaseComplete);
            asm.PhaseStarted += new EventHandler<Assembler.PhaseEventArgs>(asm_PhaseStarted);

            SetAssemblerOptions(asm);

            var output = asm.Assemble();
            if (output == null) {
                ShowErrors(asm.GetErrors());
            } else {
                WriteAssemblerOutput(asm, output);
            }
        }

        private static void WriteAssemblerOutput(Assembler asm, byte[] output) {
            bool isPseudoFile;
            bool isIPS = asm.HasPatchSegments;
            string outputExtension = isIPS ? ".ips" : ".bin";

            if (destFile == null) {
                isPseudoFile = FileReader.IsPseudoFile(sourceFile);
                if (isPseudoFile) {
                    // Pseudo-files such as %input% and %clip% shouldn't get an extension-change
                    destFile = sourceFile;
                } else {
                    if (Path.GetExtension(sourceFile).Equals(outputExtension, StringComparison.InvariantCultureIgnoreCase)) {
                        // If the input file is stupidly named (.ips or .bin), we append the extension (e.g. input.bin.bin) so we don't overwrite the source.
                        destFile += outputExtension;
                    } else {
                        if (isIPS) {
                            destFile = Path.ChangeExtension(sourceFile, ".ips");
                        } else {
                            destFile = Path.ChangeExtension(sourceFile, ".bin");
                        }
                    }
                }
            } else {
                isPseudoFile = FileReader.IsPseudoFile(destFile);
            }


            if (isIPS) {
                if (switches.PatchOffset != null) {
                    Console.WriteLine("Warning: Output type is IPS file. Offset argument will be ignored.");
                }

                var ipsFile = CreateIPSFile(output, asm.GetPatchSegments());
                fileSystem.WriteFile(destFile, ipsFile);
                Console.WriteLine(ipsFile.Length.ToString() + " bytes written to " + destFile);
            } else if (switches.PatchOffset == null) { // .BIN file
                fileSystem.WriteFile(destFile, output);
                ////File.WriteAllBytes(destFile, output);
                Console.WriteLine(output.Length.ToString() + " bytes written to " + destFile);
            } else { // Patch into another file
                using (var file = new FileStream(destFile, FileMode.Open, FileAccess.Write)) {
                    file.Seek((int)switches.PatchOffset, SeekOrigin.Begin);
                    file.Write(output, 0, output.Length);

                    Console.WriteLine(output.Length.ToString() + " bytes written to " + destFile + " at offset 0x" + ((int)switches.PatchOffset).ToString());
                }

            }
        }

        private static void SetAssemblerOptions(Assembler asm) {
            //if (dotNotRequired == true)
            //    asm.RequireDotOnDirectives = false;
            //if (overflow == true)
            //    asm.OverflowChecking = OverflowChecking.Unsigned;
            //if (overflowSigned == true)
            //    asm.OverflowChecking = OverflowChecking.Signed;
            //if (allowInvalid == true)
            //    asm.AllowInvalidOpcodes = allowInvalid.Value;
            if (switches.DotOptional != null)
                asm.RequireDotOnDirectives = (switches.DotOptional == OnOffSwitch.OFF);
            
        }
        private static byte[] CreateIPSFile(byte[] output, IList<Romulus.PatchSegment> segments) {
            var ips = new IPS.Builder();

            for (int i = 0; i < segments.Count; i++) {
                var segment = segments[i];

                int srcStart = segment.Start;
                int srcLen = segment.Length;
                int destOffset = segment.PatchOffset;

                int destSize = Math.Min(srcLen, ushort.MaxValue);


                while (srcLen > 0) {
                    ips.AddRecord(output, srcStart, destSize, destOffset);

                    // If there was more data than would fit in one record, we write the remaining data in another record
                    srcStart += destSize;
                    destOffset += destSize;
                    srcLen -= destSize;

                    destSize = Math.Min(srcLen, ushort.MaxValue);
                }
            }

            return ips.CreateIPS();
        }

        static void asm_PhaseStarted(object sender, Assembler.PhaseEventArgs e) {
            Console.Write(e.Message);
        }
        static void asm_PhaseComplete(object sender, Assembler.PhaseEventArgs e) {
            Console.WriteLine(e.Message);

        }
        private static void ShowErrors(IList<ErrorDetail> errors) {
            Console.WriteLine();
            Console.WriteLine();

            for (int i = 0; i < errors.Count; i++) {
                var error = errors[i];
                Console.WriteLine(error.File + " (" + error.LineNumber.ToString() + ") " + error.Code.ToString().Replace('_',' ') + ": " + error.Message);
            }
            Console.WriteLine("Assemble failed.");
        }


        #region Command line parsing
        private static void ProcessArg(string arg, bool isFirstArg, out bool exit) {
            exit = false;

            if (arg.Length != 0) {
                if (arg[0] == '-') {
                    ProcessSwitch(arg, out exit);
                    if (exit) {
                        ShowHelp();
                        return;
                    }
                    ////} else if (arg[0] == '@') {
                    ////    if (!ProcessOffset(arg)) {
                    ////        ShowHelp();
                    ////        exit = true;
                    ////        return;
                    ////    }
                } else if (isFirstArg) {
                    ProcessDest(arg);
                } else {
                    Console.WriteLine("Unrecognized parameter.");
                    ShowHelp();
                    exit = true;
                    return;
                }
            }
            return;
        }

        private static bool ProcessDest(string arg) {
            if (destFile != null) {
                Console.WriteLine("Can not specify more than one dest file.");
                return false;
            }

            destFile = arg;
            return true;
        }

        //private static bool ProcessOffset(string arg) {
        //    if (offset != null) {
        //        Console.WriteLine("Can not specify more than one offset.");
        //        return false;
        //    }

        //    bool hex;
        //    if (arg.Length > 1 && arg[1] == '$') {
        //        hex = true;
        //        arg = arg.Substring(2);
        //    } else {
        //        hex = false;
        //        arg = arg.Substring(1);
        //    }

        //    System.Globalization.NumberStyles style = hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer;
        //    int offsetValue;
        //    if (int.TryParse(arg, style, null, out offsetValue)) {
        //        offset = offsetValue;
        //        return true;
        //    } else {
        //        Console.WriteLine("Invalid offset value.");
        //        return false;
        //    }
        //}

        private static bool ProcessSwitch(string arg, out bool error) {
            error = false;
            
            // Parse out switch name and parameter, and remove leading "-"
            string switchName;
            string switchValue = null;

            int iColon = arg.IndexOf(':');
            if (iColon > 0) {
                switchName = arg.Substring(1, iColon - 1).ToUpper();
                switchValue = arg.Substring(iColon + 1);
            } else {
                switchName = arg.Substring(1).ToUpper();
            }

            switch (switchName) {
                case "OFFSET":
                    if (switches.PatchOffset == null) {
                        ParseOffset(switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "CHECKING":
                    if (switches.Checking == null) {
                        ParseChecking(switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "DOT":
                    if (switches.DotOptional == null) {
                        if (arg == null)
                            switches.DotOptional = OnOffSwitch.ON;
                        else
                            switches.DotOptional = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "COLON":
                    if (switches.ColonOptional == null) {
                        if (arg == null)
                            switches.ColonOptional = OnOffSwitch.ON;
                        else
                            switches.ColonOptional = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "ASM6":
                    if (switches.Asm6Mode == null) {
                        if (switchValue == null) {
                            switches.Asm6Mode = OnOffSwitch.ON;
                            switches.ColonOptional = OnOffSwitch.OFF;
                            switches.DotOptional = OnOffSwitch.OFF;
                        } else {
                            switches.Asm6Mode = ParseOnOff(switchName, switchValue, out error);
                            OnOffSwitch colonAndDotRequired = switches.Asm6Mode == OnOffSwitch.ON ? OnOffSwitch.OFF : OnOffSwitch.ON;
                            switches.ColonOptional = colonAndDotRequired;
                            switches.DotOptional = colonAndDotRequired;

                        }
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "INVALID":
                    if (switches.InvalidOpsAllowed == null) {
                        if (switchValue == null)
                            switches.InvalidOpsAllowed = OnOffSwitch.ON;
                        else
                            switches.InvalidOpsAllowed = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                case "IPS":
                    if (switches.IpsOutput == null) {
                        if (switchValue == null)
                            switches.IpsOutput = OnOffSwitch.ON;
                        else
                            switches.IpsOutput = ParseOnOff(switchName, switchValue, out error);
                    } else {
                        ShowDuplicateSwitchError(switchName);
                        error = true;
                    }
                    break;
                default:
                    Console.WriteLine("Invalid switch: " + arg);
                    Console.WriteLine();
                    ShowHelp();
                    error = true;
                    break;
            }

            return true;
            ////arg = arg.Substring(1).ToUpper();
            ////if (arg == "O") {
            ////    if (overflow != null) {
            ////        Console.WriteLine("Duplicate switch.");
            ////        return false;
            ////    }
            ////    overflow = true;
            ////} else if (arg == "OS") {
            ////    if (overflowSigned != null) {
            ////        Console.WriteLine("Duplicate switch.");
            ////        return false;
            ////    }
            ////    overflowSigned = true;
            ////} else if (arg == "I") {
            ////    if (allowInvalid != null) {
            ////        Console.WriteLine("Duplicate switch.");
            ////        return false;
            ////    }
            ////    allowInvalid = true;
            ////} else if (arg == "D") {
            ////    if (dotNotRequired != null) {
            ////        Console.WriteLine("Duplicate switch.");
            ////        return false;
            ////    }
            ////    dotNotRequired = true;
            ////} else {
            ////    Console.WriteLine("Unrecognized switch.");
            ////    return false;
            ////}
            ////return true;
        }

        private static OnOffSwitch? ParseOnOff(string switchName, string value, out bool invalid) {
            invalid = false;

            if (value.Equals("ON", StringComparison.InvariantCultureIgnoreCase)) {
                return OnOffSwitch.ON;
            } else if (value.Equals("OFF", StringComparison.InvariantCultureIgnoreCase)) {
                return OnOffSwitch.OFF;
            } else {
                Console.WriteLine("Value " + value + " is invalid for -" + switchName);
                Console.WriteLine();
                ShowHelp();

                invalid = true;
                return null;
            }
        }

        private static void ParseChecking(string value, out bool invalid) {
            invalid = false;

            switch (value.ToUpper()) {
                case "ON":
                    switches.Checking = CheckingSwitch.ON;
                    break;
                case "OFF":
                    switches.Checking = CheckingSwitch.OFF;
                    break;
                case "SIGNED":
                    switches.Checking = CheckingSwitch.SIGNED;
                    break;
                default:
                    Console.WriteLine("Value " + value + " is invalid for -CHEKCING");
                    Console.WriteLine();
                    ShowHelp();

                    invalid = true;
                    break;
            }
        }

        private static void ParseOffset(string value, out bool invalid) {
            invalid = false;

            if (value.Length == 0) { 
                invalid = true; 
                return; 
            }

            bool hex = false;
            if (value.StartsWith("$")) {
                hex = true;
                value = value.Substring(1);
            } else if (value.StartsWith("0x", StringComparison.InvariantCultureIgnoreCase)) {
                hex = true;
                value = value.Substring(2);
            }

            int offset;
            bool valid;
            if (hex) {
                valid = int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out offset);
            } else {
                valid = int.TryParse(value, out offset);
            }

            if (valid)
                switches.PatchOffset = offset;
            else {
                Console.WriteLine("Invalid patch offset specified.");
                Console.WriteLine();
                ShowHelp();
            }
        }

        private static void ShowDuplicateSwitchError(string swicth) {
            Console.WriteLine("Duplicate switch: " + swicth);
            Console.WriteLine();
            ShowHelp();
        }
        #endregion


        const string HelpText = 
@"snarfblASM 6502 assembler - syntax
    snarfblasm sourceFile [destFile] [switches]
    
    switches:
        -CHECKING:OFF/ON/SIGNED
            Overflow checking in expressions
        -OFFSET:value
            value should be a decimal, $hex, or 0xhex offset to 
            patch the dest file
        -DOT[:OFF/ON]
            Optional dots are enabled for directives (ON)
        -COLON[:OFF/ON]
            Optional colons are enabled for labels (ON)
        -ASM6[:OFF/ON]
            ASM6-like syntax (same as -DOT:ON -COLON:ON)
        -INVALID[:OFF/ON]
            Invalid opcodes are allowed (ON)
        -IPS[:OFF/ON]
            Output IPS format (ON)

    Example: snarfblasm source.asm -CHECKING:ON -ASM6 -IPS:OFF
";
        private static void ShowHelp() {

            Console.WriteLine(HelpText);
            ////Console.WriteLine("syntax: snarfblasm sourceFile [destFile] [-o | -os] [-i] [@offset]");
            ////Console.WriteLine("example: snarfblasm asmHack.asm someRom.nes -i @$1C010");
            ////Console.WriteLine("");
            ////Console.WriteLine("    sourceFile - Source ASM file");
            ////Console.WriteLine("    destFile   - Destination file");
            ////Console.WriteLine("    offset     - Destination offset (decimal or $hex). If specified, part of dest file is overwritten.");
            ////Console.WriteLine("Switches:");
            ////Console.WriteLine("    -o    Overflow checking");
            ////Console.WriteLine("    -os   Overflow checking (signed)");
            ////Console.WriteLine("    -i    Allow invalid opcodes");
        }

        static FileReader fileSystem = new FileReader();

        static string sourceFile;
        static string destFile;

        ////static bool? overflow;
        ////static bool? dotNotRequired;
        ////static bool? overflowSigned;
        ////static int? offset;
        ////static bool? allowInvalid;

        class FileReader : IFileSystem
        {
            public const string Psuedo_Form = "%form%";
            public const string Pseudo_Clip = "%clip%";

            #region IFileSystem Members

            public string GetFileText(string filename) {
                if (filename.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) {
                    return snarfblasm.TextForm.GetText();
                } else if (filename.Equals(Pseudo_Clip, StringComparison.InvariantCultureIgnoreCase)) {
                    return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                } else {
                    return System.IO.File.ReadAllText(filename);
                }
            }


            public void WriteFile(string filename, byte[] data) {
                if (filename.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) {
                    TextForm.GetText(Romulus.Hex.FormatHex(data));
                } else if (filename.Equals(Pseudo_Clip, StringComparison.InvariantCultureIgnoreCase)) {
                    if (data.Length == 0)
                        Clipboard.SetText(" ");
                    else
                        Clipboard.SetText(Romulus.Hex.FormatHex(data));
                } else {
                    // Derp - switches.PatchOffset shouldn't be handled here

                    ////if (switches.PatchOffset == null) {
                        File.WriteAllBytes(filename, data);
                    ////} else {
                    ////    using (var file = File.Open(filename, FileMode.Open)) {
                    ////        file.Seek(switches.PatchOffset.Value, SeekOrigin.Begin);

                    ////        BinaryWriter w = new BinaryWriter(file);
                    ////        w.Write(data);
                    ////    }
                    ////}
                }
            }

            #endregion

            public static bool IsPseudoFile(string file) {
                if (file == null) return false;
                if (file.Length < 3) return false;
                if (file[0] == '%' && file[file.Length - 1] == '%') return true;
                return false;
            }

            #region IFileSystem Members


            public long GetFileSize(string filename) {
                if (filename.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) return 0;

                //   System.Security.SecurityException:
                //     The caller does not have the required permission.
                //
                //   System.ArgumentException:
                //     The file name is empty, contains only white spaces, or contains invalid characters.
                //
                //   System.UnauthorizedAccessException:
                //     Access to fileName is denied.
                //
                //   System.IO.PathTooLongException:
                //     The specified path, file name, or both exceed the system-defined maximum
                //     length. For example, on Windows-based platforms, paths must be less than
                //     248 characters, and file names must be less than 260 characters.
                //
                //   System.NotSupportedException:
                //     fileName contains a colon (:) in the middle of the string.
                try {
                    return new FileInfo(filename).Length;
                } catch (System.Security.SecurityException) {
                    return -1;
                } catch (System.IO.IOException) {
                    return -1;
                } catch (ArgumentException) {
                    return -1;
                } catch (UnauthorizedAccessException) {
                    return -1;
                }
            }

            public Stream GetFileReadStream(string filename) {
                return new FileStream(filename, FileMode.Open);
            }

            public bool FileExists(string name) {
                if (name.Equals(Psuedo_Form, StringComparison.InvariantCultureIgnoreCase)) return true;

                return File.Exists(name);
            }

            #endregion

        }

    }


    struct ProgramSwitches
    {
        public int? PatchOffset;
        public CheckingSwitch? Checking;
        public OnOffSwitch? DotOptional;
        public OnOffSwitch? ColonOptional;
        public OnOffSwitch? Asm6Mode;
        public OnOffSwitch? InvalidOpsAllowed;
        public OnOffSwitch? IpsOutput;

    }

    enum CheckingSwitch
    {
        OFF,
        ON,
        SIGNED
    }
    enum OnOffSwitch
    {
        ON,
        OFF
    }
}
