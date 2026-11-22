using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Multiplayer.Common
{
    public class ByteWriter
    {
        private MemoryStream stream;
        private byte[]? scalarBuffer;
        public object? context;

        public int Position => (int)stream.Position;

        public ByteWriter(int capacity = 0)
        {
            stream = new MemoryStream(capacity);
        }

        public virtual void WriteByte(byte val) => stream.WriteByte(val);

        public virtual void WriteSByte(sbyte val) => stream.WriteByte((byte)val);

        public virtual void WriteShort(short val) => WriteScalar(unchecked((ushort)val), sizeof(short));

        public virtual void WriteUShort(ushort val) => WriteScalar(val, sizeof(ushort));

        public virtual void WriteInt32(int val) => WriteScalar(unchecked((uint)val), sizeof(int));

        public virtual void WriteUInt32(uint val) => WriteScalar(val, sizeof(uint));

        public virtual void WriteLong(long val) => WriteScalar(unchecked((ulong)val), sizeof(long));

        public virtual void WriteULong(ulong val) => WriteScalar(val, sizeof(ulong));

        public virtual unsafe void WriteFloat(float val) => WriteScalar(*(uint*)&val, sizeof(float));

        public virtual void WriteDouble(double val) => WriteScalar(unchecked((ulong)BitConverter.DoubleToInt64Bits(val)), sizeof(double));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteScalar(ulong bits, int size)
        {
            var buffer = scalarBuffer ??= new byte[sizeof(ulong)];
            for (int i = 0; i < size; i++)
            {
                buffer[i] = unchecked((byte)bits);
                bits >>= 8;
            }

            // ByteReader uses BitConverter, so preserve its byte order on every platform.
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(buffer, 0, size);

            stream.Write(buffer, 0, size);
        }

        public virtual void WriteBool(bool val) => stream.WriteByte(val ? (byte)1 : (byte)0);

        public virtual void WritePrefixedBytes(byte[]? bytes)
        {
            if (bytes == null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(bytes.Length);
            WriteRaw(bytes);
        }

        public virtual void WritePrefixedInts(IList<int> ints)
        {
            WriteInt32(ints.Count);
            foreach (var @int in ints)
                WriteInt32(@int);
        }

        public virtual void WritePrefixedUInts(IList<uint> ints)
        {
            WriteInt32(ints.Count);
            foreach (var @int in ints)
                WriteUInt32(@int);
        }

        public virtual void WriteRaw(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        public virtual void WriteFrom(byte[] buffer, int offset, int length)
        {
            stream.Write(buffer, offset, length);
        }

        public virtual ByteWriter WriteString(string? s)
        {
            WritePrefixedBytes(s == null ? null : Encoding.UTF8.GetBytes(s));
            return this;
        }

        public virtual void WriteEnum<T>(T value) where T : Enum
        {
            Type type = Enum.GetUnderlyingType(typeof(T) == typeof(Enum) ? value.GetType() : typeof(T));

            if (type == typeof(byte))
            {
                WriteByte(Convert.ToByte(value));
            }
            else if (type == typeof(sbyte))
            {
                WriteSByte(Convert.ToSByte(value));
            }
            else if (type == typeof(short))
            {
                WriteShort(Convert.ToInt16(value));
            }
            else if (type == typeof(ushort))
            {
                WriteUShort(Convert.ToUInt16(value));
            }
            else if (type == typeof(int))
            {
                WriteInt32(Convert.ToInt32(value));
            }
            else if (type == typeof(uint))
            {
                WriteUInt32(Convert.ToUInt32(value));
            }
            else if (type == typeof(long) || type == typeof(IntPtr))
            {
                WriteLong(Convert.ToInt64(value));
            }
            else if (type == typeof(ulong) || type == typeof(UIntPtr))
            {
                WriteULong(Convert.ToUInt64(value));
            }
            else
            {
                ServerLog.Error($"MP ByteWriter.WriteEnum: Unknown type {type}");
            }
        }
        private void Write(object obj)
        {
            if (obj is int @int)
            {
                WriteInt32(@int);
            }
            else if (obj is ushort @ushort)
            {
                WriteUShort(@ushort);
            }
            else if (obj is short @short)
            {
                WriteShort(@short);
            }
            else if (obj is bool @bool)
            {
                WriteBool(@bool);
            }
            else if (obj is long @long)
            {
                WriteLong(@long);
            }
            else if (obj is ulong @ulong)
            {
                WriteULong(@ulong);
            }
            else if (obj is byte @byte)
            {
                WriteByte(@byte);
            }
            else if (obj is float @float)
            {
                WriteFloat(@float);
            }
            else if (obj is double @double)
            {
                WriteDouble(@double);
            }
            else if (obj is byte[] bytes)
            {
                WritePrefixedBytes(bytes);
            }
            else if (obj is Enum enumObj)
            {
                WriteEnum(enumObj);
            }
            else if (obj is string @string)
            {
                WriteString(@string);
            }
            else if (obj is Array arr)
            {
                Write(arr.Length);
                foreach (object o in arr)
                    Write(o);
            }
            else if (obj is IList list)
            {
                Write(list.Count);
                foreach (object o in list)
                    Write(o);
            }
            else if (obj is ITuple tuple)
            {
                for (int i = 0; i < tuple.Length; i++)
                    Write(tuple[i]);
            }
            else
            {
                ServerLog.Error($"MP ByteWriter.Write: Unknown type {obj.GetType()}");
            }
        }

        public byte[] ToArray()
        {
            return stream.ToArray();
        }

        /// <summary>
        /// Writes all objects in the order given and returns the resulting bytes.
        /// </summary>
        public static byte[] GetBytes(params object[] data)
        {
            var writer = new ByteWriter();
            foreach (object o in data)
                writer.Write(o);
            return writer.ToArray();
        }

        public void SetLength(long value)
        {
            stream.SetLength(value);
        }
    }

    public class WriterException : Exception
    {
        public WriterException(string msg) : base(msg)
        {
        }
    }

}
