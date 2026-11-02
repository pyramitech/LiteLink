using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLink.Shared.Serializer
{
    internal struct Buffer
    {
        internal const int BUFFER_SIZE = 8192;

        internal byte[] Array;
        internal int Length = 0;

        public Buffer()
        {
            Array = new byte[BUFFER_SIZE];
        }

        public Buffer(int length)
        {
            Array = new byte[length];
        }

        public Buffer(byte[] array)
        {
            Array = array;
        }
    }
}
