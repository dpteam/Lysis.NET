using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Lysis;

namespace AMXModX
{
    public class AMXModXFile : PawnFile
    {
        public const uint MAGIC2 = 0x414D5858;
        private const uint MAGIC2_VERSION = 0x0300;
        private const uint AMX_MAGIC = 0xF1E0;
        private const uint AMX_DBG_MAGIC = 0xF1EF;
        private const uint MIN_FILE_VERSION = 6;
        private const uint MIN_DEBUG_FILE_VERSION = 8;
        private const uint CUR_FILE_VERSION = 8;
        private const int DEFSIZE = 8;
        private const int AMX_FLAG_DEBUG = 0x2;
        private const byte IDENT_VARIABLE = 1;
        private const byte IDENT_REFERENCE = 2;
        private const byte IDENT_ARRAY = 3;
        private const byte IDENT_REFARRAY = 4;
        private const byte IDENT_FUNCTION = 9;
        private const byte IDENT_VARARGS = 11;
        
        private class PluginHeader
        {
            public byte cellsize;
            public int disksize;
            public int imagesize;
            public int memsize;
            public int offset;
        }
        
        private class AMX_HEADER
        {
            public int size;
            public ushort magic;
            public byte file_version;
            public byte amx_version;
            public short flags;
            public short defsize;
            public int cod;
            public int dat;
            public int hea;
            public int stp;
            public int cip;
            public int publics;
            public int natives;
            public int libraries;
            public int pubvars;
            public int tags;
            public int nametable;
        }
        
        private class AMX_DEBUG_HDR
        {
            public int size;
            public ushort magic;
            public byte file_version;
            public byte amx_version;
            public short flags;
            public short files;
            public short lines;
            public short symbols;
            public short tags;
            public short automatons;
            public short states;
            
            public const int SIZE = 4 +
                                    2 +
                                    (1 * 2) +
                                    (4 * 7);
        }
        
        private Variable[] variables_;
        private Tag[] tags_;
        private byte[] DAT_;
        
        public AMXModXFile(byte[] binary)
        {
            BinaryReader reader = new BinaryReader(new MemoryStream(binary)); 
            uint magic = reader.ReadUInt32();
            
            switch (magic)
            {
              case MAGIC2:
              {
                uint version = reader.ReadUInt16();
                if (version > MAGIC2_VERSION)
                    throw new Exception("unexpected version");
                
                PluginHeader ph = null;
                uint numPlugins = reader.ReadByte();
                for (uint i = 0; i < numPlugins; i++)
                {
                    PluginHeader p = new PluginHeader();
                    p.cellsize = reader.ReadByte();
                    p.disksize = reader.ReadInt32();
                    p.imagesize = reader.ReadInt32();
                    p.memsize = reader.ReadInt32();
                    p.offset = reader.ReadInt32();
                    if (p.cellsize == 4)
                    {
                        ph = p;
                        break;
                    }
                }
                if (ph == null)
                    throw new Exception("could not find applicable cell size");
                
                int bufferSize = ph.imagesize > ph.memsize
                                 ? ph.imagesize + 1
                                 : ph.memsize + 1;
                byte[] bits = new byte[bufferSize];
         
                MemoryStream ms = new MemoryStream(binary, ph.offset + 2, ph.disksize - 2);
                DeflateStream gzip = new DeflateStream(ms, CompressionMode.Decompress);
                int read = gzip.Read(bits, 0, bufferSize);
                if (read != ph.imagesize)
                    throw new Exception("uncompress error");
                binary = bits;
                break;
              }
                
              default:
                throw new Exception("unrecognized file");
            }
            
            reader = new BinaryReader(new MemoryStream(binary));
            AMX_HEADER amx = new AMX_HEADER();
            amx.size = reader.ReadInt32();
            amx.magic = reader.ReadUInt16();
            amx.file_version = reader.ReadByte();
            amx.amx_version = reader.ReadByte();
            amx.flags = reader.ReadInt16();
            amx.defsize = reader.ReadInt16();
            amx.cod = reader.ReadInt32();
            amx.dat = reader.ReadInt32();
            amx.hea = reader.ReadInt32();
            amx.stp = reader.ReadInt32();
            amx.cip = reader.ReadInt32();
            amx.publics = reader.ReadInt32();
            amx.natives = reader.ReadInt32();
            amx.libraries = reader.ReadInt32();
            amx.pubvars = reader.ReadInt32();
            amx.tags = reader.ReadInt32();
            amx.nametable = reader.ReadInt32();
            
            if (amx.magic != AMX_MAGIC)
                throw new Exception("unrecognized amx header");
            
            if (amx.file_version < MIN_FILE_VERSION ||
                amx.file_version > CUR_FILE_VERSION)
            {
                throw new Exception("unrecognized amx version");
            }
            
            if (amx.defsize != DEFSIZE)
                throw new Exception("unrecognized header defsize");
            
            DAT_ = new byte[amx.hea - amx.dat];
            for (int i = 0; i < DAT_.Length; i++)
                DAT_[i] = binary[amx.dat + i];
            
            if (amx.publics > 0)
            {
                int count = (amx.natives - amx.publics) / DEFSIZE;
                publics_ = new Public[count];
                BinaryReader r = new BinaryReader(new MemoryStream(binary, amx.publics, count * DEFSIZE));
                for (int i = 0; i < publics_.Length; i++)
                {
                    uint address = r.ReadUInt32();
                    int nameoffset = r.ReadInt32();
                    string name = ReadName(binary, nameoffset);
                    publics_[i] = new Public(name, address);
                }
            }
            
            if (amx.file_version >= MIN_DEBUG_FILE_VERSION &&
                (amx.flags & AMX_FLAG_DEBUG) == AMX_FLAG_DEBUG)
            {
                int debugOffset = amx.size;
                BinaryReader r = new BinaryReader(new MemoryStream(binary, debugOffset, AMX_DEBUG_HDR.SIZE));
                AMX_DEBUG_HDR dbg = new AMX_DEBUG_HDR();
                dbg.size = r.ReadInt32();
                dbg.magic = r.ReadUInt16();
                dbg.file_version = r.ReadByte();
                dbg.amx_version = r.ReadByte();
                dbg.flags = r.ReadInt16();
                dbg.files = r.ReadInt16();
                dbg.lines = r.ReadInt16();
                dbg.symbols = r.ReadInt16();
                dbg.tags = r.ReadInt16();
                dbg.automatons = r.ReadInt16();
                dbg.states = r.ReadInt16();
                
                if (dbg.magic != AMX_DBG_MAGIC)
                    throw new Exception("unrecognized debug magic");
                
                r = new BinaryReader(new MemoryStream(binary, debugOffset + AMX_DEBUG_HDR.SIZE,
                                                      dbg.size - AMX_DEBUG_HDR.SIZE));
                
                // Read files.
                for (short i = 0; i < dbg.files; i++)
                {
                    uint offset = r.ReadUInt32();
                    string name = ReadName(r);
                }
                
                // Read lines.
                for (short i = 0; i < dbg.lines; i++)
                {
                    uint offset = r.ReadUInt32();
                    int lineno = r.ReadInt32();
                }
                
                List<Function> functions = new List<Function>();
                List<Variable> globals = new List<Variable>();
                List<Variable> locals = new List<Variable>();
                
                // Read symbols.
                for (short i = 0; i < dbg.symbols; i++)
                {
                    int addr = r.ReadInt32();
                    ushort tagid = r.ReadUInt16();
                    uint codestart = r.ReadUInt32();
                    uint codeend = r.ReadUInt32();
                    byte ident = r.ReadByte();
                    Scope vclass = (Scope)r.ReadByte();
                    ushort dimcount = r.ReadUInt16();
                    string name = ReadName(r);
                    
                    if (ident == IDENT_FUNCTION)
                    {
                        Function func = new Function((uint)addr, codestart, codeend, name, tagid);
                        functions.Add(func);
                    }
                    else 
                    {
                        VariableType type = FromIdent(ident);
                        Dimension[] dims = null;
                        if (dimcount > 0)
                        {
                            dims = new Dimension[dimcount];
                            for (ushort j = 0; j < dimcount; j++)
                            {
                                short tag_id = r.ReadInt16();
                                int size = r.ReadInt32();
                                dims[j] = new Dimension(tag_id, null, size);
                            }
                        }
                        
                        Variable var = new Variable(addr, tagid, null, codestart, codeend, type, vclass, name, dims);
                        if (vclass == Scope.Global)
                            globals.Add(var);
                        else
                            locals.Add(var);
                    }
                }
                
                globals.Sort(delegate(Variable var1, Variable var2)
                {
                    return var1.address - var2.address;
                });
                functions.Sort(delegate(Function fun1, Function fun2)
                {
                    return (int)(fun1.address - fun2.address);
                });

                variables_ = locals.ToArray();
                globals_ = globals.ToArray();
                functions_ = functions.ToArray();
                
                // Find tags.
                tags_ = new Tag[dbg.tags];
                for (short i = 0; i < dbg.tags; i++)
                {
                    uint tag_id = r.ReadUInt16();
                    string name = ReadName(r);
                    tags_[i] = new Tag(name, tag_id);
                }
                
                // Update symbols.
                for (int i = 0; i < functions_.Length; i++)
                    functions_[i].setTag(findTag(functions_[i].tag_id));
                for (int i = 0; i < variables_.Length; i++)
                    variables_[i].setTag(findTag(variables_[i].tag_id));
                for (int i = 0; i < globals_.Length; i++)
                    globals_[i].setTag(findTag(globals_[i].tag_id));
            }
        }
        
        private static string ReadName(BinaryReader r)
        {
            List<byte> buffer = new List<byte>();
            do
            {
                byte b = r.ReadByte();
                if (b == 0)
                    break;
                buffer.Add(b);
            } while (true);
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        
        private static string ReadName(byte[] bytes, int offset)
        {
            int count = offset;
            for (; count < bytes.Length; count++)
            {
                if (bytes[count] == 0)
                    break;
            }
            return System.Text.Encoding.UTF8.GetString(bytes, offset, count - offset);
        } 
        
        private static string ReadString(byte[] bytes, int offset)
        {
            List<byte> buffer = new List<byte>();
            int count = offset;
            for (; count < bytes.Length; count += 4)
            {
                if (bytes[count] == 0)
                    break;
                int cell = BitConverter.ToInt32(bytes, count);
                buffer.Add((byte)cell);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray(), 0, buffer.Count);
        }
        
        private static VariableType FromIdent(byte ident)
        {
            switch (ident)
            {
                case IDENT_VARIABLE:
                    return VariableType.Normal;
                case IDENT_REFERENCE:
                    return VariableType.Reference;
                case IDENT_ARRAY:
                    return VariableType.Array;
                case IDENT_REFARRAY:
                    return VariableType.ArrayReference;
                case IDENT_VARARGS:
                    return VariableType.Variadic;
                default:
                    return VariableType.Normal;
            }
        }
        
        private Tag findTag(uint tag_id)
        {
            for (int i = 0; i < tags_.Length; i++)
            {
                if (tags_[i].tag_id == tag_id)
                    return tags_[i];
            }
            return null;
        }
        
        public override string stringFromData(int address)
        {
            return ReadString(DAT, address);
        }
        public override float floatFromData(int address)
        {
            return BitConverter.ToSingle(DAT, address);
        }
        public override int int32FromData(int address)
        {
            return BitConverter.ToInt32(DAT, address);
        }
        public override byte[] DAT
        {
            get { return DAT_; }
        }
    }
}

