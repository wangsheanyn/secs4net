using System;
namespace Secs4Net {
    public enum SecsFormat : byte {
        List    = 0b00_00_00_00,
        Binary  = 0b00_10_00_00,
        Boolean = 0b00_10_01_00,
        ASCII   = 0b01_00_00_00,
        JIS8    = 0b01_00_01_00,
        I8      = 0b01_10_00_00,
        I1      = 0b01_10_01_00,
        I2      = 0b01_10_10_00,
        I4      = 0b01_11_00_00,
        F8      = 0b10_00_00_00,
        F4      = 0b10_01_00_00,
        U8      = 0b10_10_00_00,
        U1      = 0b10_10_01_00,
        U2      = 0b10_10_10_00,
        U4      = 0b10_11_00_00,
    }
}