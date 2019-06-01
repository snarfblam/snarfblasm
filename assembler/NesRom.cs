using System;
using System.Collections.Generic;
using System.Text;

namespace snarfblasm
{
    public static class NesRom
    {
        public const int BankSize = 0x4000;
        public const int HeaderSize = 0x10;
        public static int GetOffset(int bankIndex, int address) {
            return HeaderSize + BankSize * bankIndex + (address & 0x3FFF);
        }
    }
}
