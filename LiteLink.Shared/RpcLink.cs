namespace LiteLink
{

    public struct PermissionBitmask
    {
        public ulong Bitmask1 { get; set; } = 0;
        public ulong Bitmask2 { get; set; } = 0;

        public PermissionBitmask(ulong bitmask1 = 0, ulong bitmask2 = 0)
        {
            Bitmask1 = bitmask1;
            Bitmask2 = bitmask2;
        }

        public void SetBitmask(PermissionBitmask bitmask)
        {
            Bitmask1 |= bitmask.Bitmask1;
            Bitmask2 |= bitmask.Bitmask2;
        }

        public void FlipBitmask(PermissionBitmask bitmask)
        {
            Bitmask1 ^= bitmask.Bitmask1;
            Bitmask2 ^= bitmask.Bitmask2;
        }

        public bool Compare(PermissionBitmask bitmask)
        {
            return ((Bitmask1 & bitmask.Bitmask1) | (Bitmask2 & bitmask.Bitmask2)) != 0;
        }
    }

    public ref struct RequestHeader
    {
        public ushort Opcode;
        public bool Compressed;
    }

    public enum ErrorCodes : byte
    {
        Success = 0x00, //Request passed successfully
        BadRequest = 0x01,
        Unauthorized = 0x02,
        Uninitialized = 0x03,
        ShuttingDown = 0x04,

        Unhandled = 0xff, //For unhandled exceptions
    }

    public ref struct ResponseHeader
    {
        public ErrorCodes Code;
        public bool Compressed;
        public int Count;
    }


}
