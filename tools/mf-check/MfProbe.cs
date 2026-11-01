// Black-box Media Foundation probe, built on the VM with the in-box .NET Framework compiler (C# 5).
//
//   mfprobe.exe probe <file> <out.json> [firstSamples]
//   mfprobe.exe thumb <file> <out.png>
//
// It uses only documented, public Windows APIs: the Media Foundation source resolver, presentation descriptors,
// source reader and ASF content-info interfaces (mfidl.h / mfreadwrite.h / wmcontainer.h as published in the Windows
// SDK documentation), and the Shell's IShellItemImageFactory for thumbnails. It reads no Media Foundation code.
//
// The probe writes one JSON document per file. Every section sits in its own try block, so a failing API call is
// recorded with its HRESULT and the rest of the document is still written.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MfCheck
{
    // ---------------------------------------------------------------- COM declarations (mfobjects.h, mfidl.h, ...)

    [StructLayout(LayoutKind.Sequential)]
    struct PV
    {
        // PROPVARIANT: vt, three reserved words, then a 16-byte union (x64 layout; the struct is over-sized on x86).
        public ushort vt;
        public ushort r1, r2, r3;
        public long a;
        public long b;
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        void GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int type);
        void CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
        void GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        void GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out double value);
        void GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        void GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint length);
        void GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size, out uint length);
        void GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr value, out uint length);
        void GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint size);
        void GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buf, uint size, out uint written);
        void GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr buf, out uint size);
        void GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr obj);
        void SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        void DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key);
        void DeleteAllItems();
        void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        void SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, double value);
        void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        void SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPArray)] byte[] buf, uint size);
        void SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr obj);
        void LockStore();
        void UnlockStore();
        void GetCount(out uint count);
        void GetItemByIndex(uint index, out Guid key, ref PV value);
        void CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        // COM interop does not flatten interface inheritance into the vtable: the 30 IMFAttributes slots are
        // declared here as placeholders so the methods below land on their real slots (mfobjects.h order).
        // Attribute access goes through a cast to IMFAttributes, which queries that interface separately.
        void Attr00();
        void Attr01();
        void Attr02();
        void Attr03();
        void Attr04();
        void Attr05();
        void Attr06();
        void Attr07();
        void Attr08();
        void Attr09();
        void Attr10();
        void Attr11();
        void Attr12();
        void Attr13();
        void Attr14();
        void Attr15();
        void Attr16();
        void Attr17();
        void Attr18();
        void Attr19();
        void Attr20();
        void Attr21();
        void Attr22();
        void Attr23();
        void Attr24();
        void Attr25();
        void Attr26();
        void Attr27();
        void Attr28();
        void Attr29();
        void GetMajorType(out Guid type);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        void GetRepresentation(Guid representation, out IntPtr rep);
        void FreeRepresentation(Guid representation, IntPtr rep);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        void Unlock();
        void GetCurrentLength(out uint length);
        void SetCurrentLength(uint length);
        void GetMaxLength(out uint length);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        // COM interop does not flatten interface inheritance into the vtable: the 30 IMFAttributes slots are
        // declared here as placeholders so the methods below land on their real slots (mfobjects.h order).
        // Attribute access goes through a cast to IMFAttributes, which queries that interface separately.
        void Attr00();
        void Attr01();
        void Attr02();
        void Attr03();
        void Attr04();
        void Attr05();
        void Attr06();
        void Attr07();
        void Attr08();
        void Attr09();
        void Attr10();
        void Attr11();
        void Attr12();
        void Attr13();
        void Attr14();
        void Attr15();
        void Attr16();
        void Attr17();
        void Attr18();
        void Attr19();
        void Attr20();
        void Attr21();
        void Attr22();
        void Attr23();
        void Attr24();
        void Attr25();
        void Attr26();
        void Attr27();
        void Attr28();
        void Attr29();
        void GetSampleFlags(out uint flags);
        void SetSampleFlags(uint flags);
        void GetSampleTime(out long time);
        void SetSampleTime(long time);
        void GetSampleDuration(out long duration);
        void SetSampleDuration(long duration);
        void GetBufferCount(out uint count);
        void GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(IMFMediaBuffer buffer);
        void RemoveBufferByIndex(uint index);
        void RemoveAllBuffers();
        void GetTotalLength(out uint length);
        void CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("e93dcf6c-4b07-4e1e-8123-aa16ed6eadf5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaTypeHandler
    {
        void IsMediaTypeSupported(IMFMediaType type, out IMFMediaType closest);
        void GetMediaTypeCount(out uint count);
        void GetMediaTypeByIndex(uint index, out IMFMediaType type);
        void SetCurrentMediaType(IMFMediaType type);
        void GetCurrentMediaType(out IMFMediaType type);
        void GetMajorType(out Guid type);
    }

    [ComImport, Guid("56c03d9c-9dbb-45f5-ab4b-d80f47c05938"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFStreamDescriptor
    {
        // COM interop does not flatten interface inheritance into the vtable: the 30 IMFAttributes slots are
        // declared here as placeholders so the methods below land on their real slots (mfobjects.h order).
        // Attribute access goes through a cast to IMFAttributes, which queries that interface separately.
        void Attr00();
        void Attr01();
        void Attr02();
        void Attr03();
        void Attr04();
        void Attr05();
        void Attr06();
        void Attr07();
        void Attr08();
        void Attr09();
        void Attr10();
        void Attr11();
        void Attr12();
        void Attr13();
        void Attr14();
        void Attr15();
        void Attr16();
        void Attr17();
        void Attr18();
        void Attr19();
        void Attr20();
        void Attr21();
        void Attr22();
        void Attr23();
        void Attr24();
        void Attr25();
        void Attr26();
        void Attr27();
        void Attr28();
        void Attr29();
        void GetStreamIdentifier(out uint id);
        void GetMediaTypeHandler(out IMFMediaTypeHandler handler);
    }

    [ComImport, Guid("03cb2711-24d7-4db6-a17f-f3a7a479a536"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFPresentationDescriptor
    {
        // COM interop does not flatten interface inheritance into the vtable: the 30 IMFAttributes slots are
        // declared here as placeholders so the methods below land on their real slots (mfobjects.h order).
        // Attribute access goes through a cast to IMFAttributes, which queries that interface separately.
        void Attr00();
        void Attr01();
        void Attr02();
        void Attr03();
        void Attr04();
        void Attr05();
        void Attr06();
        void Attr07();
        void Attr08();
        void Attr09();
        void Attr10();
        void Attr11();
        void Attr12();
        void Attr13();
        void Attr14();
        void Attr15();
        void Attr16();
        void Attr17();
        void Attr18();
        void Attr19();
        void Attr20();
        void Attr21();
        void Attr22();
        void Attr23();
        void Attr24();
        void Attr25();
        void Attr26();
        void Attr27();
        void Attr28();
        void Attr29();
        void GetStreamDescriptorCount(out uint count);
        void GetStreamDescriptorByIndex(uint index, [MarshalAs(UnmanagedType.Bool)] out bool selected, out IMFStreamDescriptor descriptor);
        void SelectStream(uint index);
        void DeselectStream(uint index);
        void Clone(out IMFPresentationDescriptor clone);
    }

    [ComImport, Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaSource
    {
        // IMFMediaEventGenerator
        void GetEvent(uint flags, out IntPtr ev);
        void BeginGetEvent(IntPtr callback, IntPtr state);
        void EndGetEvent(IntPtr result, out IntPtr ev);
        void QueueEvent(uint type, IntPtr extendedType, int hr, IntPtr value);
        // IMFMediaSource
        void GetCharacteristics(out uint characteristics);
        void CreatePresentationDescriptor(out IMFPresentationDescriptor descriptor);
        void Start(IMFPresentationDescriptor descriptor, IntPtr timeFormat, IntPtr startPosition);
        void Stop();
        void Pause();
        void Shutdown();
    }

    [ComImport, Guid("fbe5a32d-a497-4b61-bb85-97b1a848a6e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceResolver
    {
        void CreateObjectFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, uint flags, IntPtr props, out uint objectType, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void CreateObjectFromByteStream(IntPtr stream, [MarshalAs(UnmanagedType.LPWStr)] string url, uint flags, IntPtr props, out uint objectType, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void BeginCreateObjectFromURL(IntPtr a, uint b, IntPtr c, IntPtr d, IntPtr e, IntPtr f);
        void EndCreateObjectFromURL(IntPtr result, out uint objectType, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void BeginCreateObjectFromByteStream(IntPtr a, IntPtr b, uint c, IntPtr d, IntPtr e, IntPtr f, IntPtr g);
        void EndCreateObjectFromByteStream(IntPtr result, out uint objectType, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        void CancelObjectCreation(IntPtr cookie);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void GetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        void SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        void GetCurrentMediaType(uint stream, out IMFMediaType type);
        void SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid timeFormat, ref PV position);
        void ReadSample(uint stream, uint controlFlags, out uint actualStream, out uint streamFlags, out long timestamp, out IMFSample sample);
        void Flush(uint stream);
        void GetServiceForStream(uint stream, [In, MarshalAs(UnmanagedType.LPStruct)] Guid service, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr obj);
        void GetPresentationAttribute(uint stream, [In, MarshalAs(UnmanagedType.LPStruct)] Guid attribute, ref PV value);
    }

    // wmcontainer.h
    [ComImport, Guid("b1dca5cd-d5da-4451-8e9e-db5c59914ead"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFASFContentInfo
    {
        void GetHeaderSize(IMFMediaBuffer startOfContent, out ulong headerSize);
        void ParseHeader(IMFMediaBuffer headerBuffer, ulong offsetWithinHeader);
        void GenerateHeader(IMFMediaBuffer header, out uint headerSize);
        void GetProfile(out IMFASFProfile profile);
        void SetProfile(IMFASFProfile profile);
        void GeneratePresentationDescriptor(out IMFPresentationDescriptor descriptor);
        void GetEncodingConfigurationPropertyStore(ushort stream, out IntPtr store);
    }

    [ComImport, Guid("d267bf6a-028b-4e0d-903d-43f0ef82d0d4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFASFProfile
    {
        // COM interop does not flatten interface inheritance into the vtable: the 30 IMFAttributes slots are
        // declared here as placeholders so the methods below land on their real slots (mfobjects.h order).
        // Attribute access goes through a cast to IMFAttributes, which queries that interface separately.
        void Attr00();
        void Attr01();
        void Attr02();
        void Attr03();
        void Attr04();
        void Attr05();
        void Attr06();
        void Attr07();
        void Attr08();
        void Attr09();
        void Attr10();
        void Attr11();
        void Attr12();
        void Attr13();
        void Attr14();
        void Attr15();
        void Attr16();
        void Attr17();
        void Attr18();
        void Attr19();
        void Attr20();
        void Attr21();
        void Attr22();
        void Attr23();
        void Attr24();
        void Attr25();
        void Attr26();
        void Attr27();
        void Attr28();
        void Attr29();
        void GetStreamCount(out uint count);
        void GetStream(uint index, out ushort streamNumber, out IMFASFStreamConfig config);
        void GetStreamByNumber(ushort streamNumber, out IMFASFStreamConfig config);
        void SetStream(IMFASFStreamConfig config);
        void RemoveStream(ushort streamNumber);
        void CreateStream(IMFMediaType type, out IMFASFStreamConfig config);
        void GetMutualExclusionCount(out uint count);
        void GetMutualExclusion(uint index, out IMFASFMutualExclusion exclusion);
        void AddMutualExclusion(IMFASFMutualExclusion exclusion);
        void RemoveMutualExclusion(uint index);
        void CreateMutualExclusion(out IMFASFMutualExclusion exclusion);
        void GetStreamPrioritization(out IntPtr p);
        void AddStreamPrioritization(IntPtr p);
        void RemoveStreamPrioritization();
        void CreateStreamPrioritization(out IntPtr p);
        void Clone(out IMFASFProfile clone);
    }

    [ComImport, Guid("9e8ae8d2-dbbd-4200-9aca-06e6df484913"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFASFStreamConfig
    {
        // COM interop does not flatten interface inheritance into the vtable: the 30 IMFAttributes slots are
        // declared here as placeholders so the methods below land on their real slots (mfobjects.h order).
        // Attribute access goes through a cast to IMFAttributes, which queries that interface separately.
        void Attr00();
        void Attr01();
        void Attr02();
        void Attr03();
        void Attr04();
        void Attr05();
        void Attr06();
        void Attr07();
        void Attr08();
        void Attr09();
        void Attr10();
        void Attr11();
        void Attr12();
        void Attr13();
        void Attr14();
        void Attr15();
        void Attr16();
        void Attr17();
        void Attr18();
        void Attr19();
        void Attr20();
        void Attr21();
        void Attr22();
        void Attr23();
        void Attr24();
        void Attr25();
        void Attr26();
        void Attr27();
        void Attr28();
        void Attr29();
        void GetStreamType(out Guid type);
        [PreserveSig] ushort GetStreamNumber();
        void SetStreamNumber(ushort number);
        void GetMediaType(out IMFMediaType type);
        void SetMediaType(IMFMediaType type);
        void GetPayloadExtensionCount(out ushort count);
        void GetPayloadExtension(ushort index, out Guid extension, out ushort dataSize, IntPtr info, ref uint infoSize);
        void AddPayloadExtension(Guid extension, ushort dataSize, IntPtr info, uint infoSize);
        void RemoveAllPayloadExtensions();
        void Clone(out IMFASFStreamConfig clone);
    }

    [ComImport, Guid("12558291-e399-11d5-bc2a-00b0d0f3f4ab"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFASFMutualExclusion
    {
        void GetExclusionType(out Guid type);
        void SetType(Guid type);
        void GetRecordCount(out uint count);
        void GetStreamsForRecord(uint record, [Out, MarshalAs(UnmanagedType.LPArray)] ushort[] streams, ref uint count);
        void AddStreamForRecord(uint record, ushort stream);
        void RemoveStreamFromRecord(uint record, ushort stream);
        void RemoveRecord(uint record);
        void AddRecord(out uint record);
        void Clone(out IMFASFMutualExclusion clone);
    }

    // shobjidl.h
    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr hbitmap);
    }

    static class Native
    {
        [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint max, out IMFMediaBuffer buffer);
        [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes attrs, uint size);
        [DllImport("mf.dll")] public static extern int MFCreateSourceResolver(out IMFSourceResolver resolver);
        [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromMediaSource(IMFMediaSource source, IMFAttributes attrs, out IMFSourceReader reader);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attrs, out IMFSourceReader reader);
        [DllImport("mf.dll")] public static extern int MFCreateASFContentInfo(out IMFASFContentInfo info);
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PV pv);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHCreateItemFromParsingName(string path, IntPtr bc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    }

    // ---------------------------------------------------------------- tiny ordered JSON writer

    class Obj
    {
        public List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
        public Obj Add(string key, object value) { Items.Add(new KeyValuePair<string, object>(key, value)); return this; }
    }

    static class Json
    {
        public static string Write(object o) { StringBuilder sb = new StringBuilder(); W(sb, o, 0); sb.Append('\n'); return sb.ToString(); }

        static void Indent(StringBuilder sb, int n) { sb.Append('\n'); sb.Append(' ', n * 2); }

        static void W(StringBuilder sb, object o, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            string s = o as string;
            if (s != null) { Str(sb, s); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is int || o is uint || o is long || o is ulong || o is ushort || o is byte) { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            Obj obj = o as Obj;
            if (obj != null)
            {
                if (obj.Items.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (int i = 0; i < obj.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Indent(sb, depth + 1);
                    Str(sb, obj.Items[i].Key); sb.Append(": ");
                    W(sb, obj.Items[i].Value, depth + 1);
                }
                Indent(sb, depth); sb.Append('}');
                return;
            }
            List<object> list = o as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Indent(sb, depth + 1);
                    W(sb, list[i], depth + 1);
                }
                Indent(sb, depth); sb.Append(']');
                return;
            }
            Str(sb, o.ToString());
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    // ---------------------------------------------------------------- probe

    static class Program
    {
        // Attribute keys published in mfapi.h / mfidl.h.
        static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid MF_MT_USER_DATA = new Guid("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
        static readonly Guid MF_PD_DURATION = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        static readonly Guid MF_SD_PROTECTED = new Guid("00af2181-bdc2-423c-abca-f503593bc121");
        static readonly Guid MF_SD_LANGUAGE = new Guid("00af2180-bdc2-423c-abca-f503593bc121");
        static readonly Guid MF_SD_STREAM_NAME = new Guid("4f1b099d-d314-41e5-a781-7fefaa4c501f");
        static readonly Guid MFSampleExtension_CleanPoint = new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
        static readonly Guid MFSampleExtension_Discontinuity = new Guid("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");
        static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        static readonly Guid MFMediaType_Protected = new Guid("7b4b6fe6-9d04-4494-be14-7e0bd076c8e4");

        const uint ANY_STREAM = 0xFFFFFFFE;
        const int MaxSampleBytesHashed = 8 * 1024 * 1024;

        static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 3 && args[0] == "probe")
                    return Probe(args[1], args[2], args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20);
                if (args.Length >= 3 && args[0] == "thumb")
                    return Thumb(args[1], args[2]);
                Console.Error.WriteLine("usage: mfprobe probe <file> <out.json> [n] | thumb <file> <out.png>");
                return 64;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
                return 2;
            }
        }

        static string Hex(uint hr) { return "0x" + hr.ToString("X8"); }

        static Obj Error(string where, Exception e)
        {
            COMException ce = e as COMException;
            Obj o = new Obj().Add("where", where).Add("message", e.Message.Trim());
            if (ce != null) o.Add("hresult", Hex((uint)ce.ErrorCode));
            return o;
        }

        static Obj HrError(string where, int hr)
        {
            string msg = "";
            try { msg = Marshal.GetExceptionForHR(hr).Message.Trim(); } catch (Exception) { }
            return new Obj().Add("where", where).Add("hresult", Hex((uint)hr)).Add("message", msg);
        }

        // -------- attribute helpers

        static bool TryUInt32(IMFAttributes a, Guid key, out uint v)
        {
            try { a.GetUINT32(key, out v); return true; } catch (COMException) { v = 0; return false; }
        }

        static bool TryUInt64(IMFAttributes a, Guid key, out ulong v)
        {
            try { a.GetUINT64(key, out v); return true; } catch (COMException) { v = 0; return false; }
        }

        static bool TryGuid(IMFAttributes a, Guid key, out Guid v)
        {
            try { a.GetGUID(key, out v); return true; } catch (COMException) { v = Guid.Empty; return false; }
        }

        static string Fourcc(uint d1)
        {
            // Media Foundation formats built from a FOURCC or wave-format tag share one base GUID tail.
            char[] c = new char[4];
            for (int i = 0; i < 4; i++)
            {
                int b = (int)((d1 >> (8 * i)) & 0xFF);
                if (b < 0x20 || b > 0x7E) return null;
                c[i] = (char)b;
            }
            return new string(c);
        }

        static Obj GuidInfo(Guid g)
        {
            Obj o = new Obj().Add("guid", g.ToString("D"));
            byte[] b = g.ToByteArray();
            uint d1 = BitConverter.ToUInt32(b, 0);
            bool baseTail = g.ToString("D").Substring(8).Equals("-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase);
            o.Add("baseGuid", baseTail);
            if (baseTail)
            {
                o.Add("data1", "0x" + d1.ToString("X8"));
                string fcc = Fourcc(d1);
                if (fcc != null) o.Add("fourcc", fcc);
            }
            return o;
        }

        static string MajorName(Guid g)
        {
            if (g == MFMediaType_Audio) return "audio";
            if (g == MFMediaType_Video) return "video";
            if (g == MFMediaType_Protected) return "protected";
            return "other";
        }

        // Every attribute, typed, so that nothing MF knows about the object is lost to a short list of keys.
        static List<object> DumpAttributes(IMFAttributes a)
        {
            List<object> list = new List<object>();
            uint count;
            a.GetCount(out count);
            for (uint i = 0; i < count; i++)
            {
                Obj item = new Obj();
                PV pv = new PV();
                try
                {
                    Guid key;
                    a.GetItemByIndex(i, out key, ref pv);
                    item.Add("key", key.ToString("D")).Add("vt", (int)pv.vt);
                    switch (pv.vt)
                    {
                        case 19: item.Add("value", (uint)pv.a); break;                       // VT_UI4
                        case 21: item.Add("value", (ulong)pv.a); break;                      // VT_UI8
                        case 5: item.Add("value", BitConverter.Int64BitsToDouble(pv.a)); break; // VT_R8
                        case 72: // VT_CLSID
                            item.Add("value", ((Guid)Marshal.PtrToStructure(new IntPtr(pv.a), typeof(Guid))).ToString("D"));
                            break;
                        case 31: item.Add("value", Marshal.PtrToStringUni(new IntPtr(pv.a))); break; // VT_LPWSTR
                        case 0x1011: // VT_VECTOR | VT_UI1, a blob: size then pointer
                            {
                                int size = (int)(uint)pv.a;
                                int take = Math.Min(size, 64);
                                byte[] bytes = new byte[take];
                                if (take > 0) Marshal.Copy(new IntPtr(pv.b), bytes, 0, take);
                                item.Add("blobSize", size).Add("blobHead", BitConverter.ToString(bytes).Replace("-", ""));
                                break;
                            }
                        default: item.Add("value", null); break;
                    }
                }
                catch (Exception e)
                {
                    item.Add("error", e.Message.Trim());
                }
                finally
                {
                    try { Native.PropVariantClear(ref pv); } catch (Exception) { }
                }
                list.Add(item);
            }
            return list;
        }

        static Obj DescribeType(IMFMediaType type)
        {
            IMFAttributes a = (IMFAttributes)type;
            Obj o = new Obj();
            Guid major, sub;
            uint u32;
            ulong u64;
            if (TryGuid(a, MF_MT_MAJOR_TYPE, out major)) o.Add("major", MajorName(major)).Add("majorType", GuidInfo(major));
            if (TryGuid(a, MF_MT_SUBTYPE, out sub)) o.Add("subtype", GuidInfo(sub));
            if (TryUInt32(a, MF_MT_AUDIO_SAMPLES_PER_SECOND, out u32)) o.Add("sampleRate", u32);
            if (TryUInt32(a, MF_MT_AUDIO_NUM_CHANNELS, out u32)) o.Add("channels", u32);
            if (TryUInt32(a, MF_MT_AUDIO_BITS_PER_SAMPLE, out u32)) o.Add("bitsPerSample", u32);
            if (TryUInt32(a, MF_MT_AUDIO_BLOCK_ALIGNMENT, out u32)) o.Add("blockAlign", u32);
            if (TryUInt32(a, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, out u32)) o.Add("avgBytesPerSecond", u32);
            if (TryUInt64(a, MF_MT_FRAME_SIZE, out u64)) o.Add("width", (uint)(u64 >> 32)).Add("height", (uint)(u64 & 0xFFFFFFFF));
            if (TryUInt64(a, MF_MT_FRAME_RATE, out u64)) o.Add("frameRateNum", (uint)(u64 >> 32)).Add("frameRateDen", (uint)(u64 & 0xFFFFFFFF));
            if (TryUInt64(a, MF_MT_PIXEL_ASPECT_RATIO, out u64)) o.Add("parNum", (uint)(u64 >> 32)).Add("parDen", (uint)(u64 & 0xFFFFFFFF));
            if (TryUInt32(a, MF_MT_AVG_BITRATE, out u32)) o.Add("avgBitrate", u32);
            if (TryUInt32(a, MF_MT_INTERLACE_MODE, out u32)) o.Add("interlaceMode", u32);
            try
            {
                uint size;
                a.GetBlobSize(MF_MT_USER_DATA, out size);
                o.Add("userDataBytes", size);
            }
            catch (COMException) { }
            o.Add("attributes", DumpAttributes(a));
            return o;
        }

        // -------- the probe

        static int Probe(string path, string outPath, int firstSamples)
        {
            Obj root = new Obj();
            List<object> errors = new List<object>();
            Stopwatch total = Stopwatch.StartNew();
            root.Add("file", Path.GetFileName(path)).Add("bytes", new FileInfo(path).Length);
            root.Add("api", "MFCreateSourceResolver + IMFPresentationDescriptor + IMFSourceReader (native media types)");

            int hr = Native.MFStartup(0x00020070, 0);
            root.Add("mfStartup", Hex((uint)hr));
            if (hr < 0) { errors.Add(HrError("MFStartup", hr)); root.Add("errors", errors); File.WriteAllText(outPath, Json.Write(root)); return 1; }

            IMFMediaSource source = null;
            IMFPresentationDescriptor pd = null;
            IMFSourceReader reader = null;
            List<uint> streamIds = new List<uint>();
            bool opened = false;

            // 1. Resolve a media source, the way an application opens a file for playback.
            try
            {
                IMFSourceResolver resolver;
                Check(Native.MFCreateSourceResolver(out resolver), "MFCreateSourceResolver");
                uint objType;
                object obj;
                resolver.CreateObjectFromURL(path, 1 /* MF_RESOLUTION_MEDIASOURCE */, IntPtr.Zero, out objType, out obj);
                source = (IMFMediaSource)obj;
                opened = true;
                root.Add("sourceResolved", true);
            }
            catch (Exception e)
            {
                root.Add("sourceResolved", false);
                errors.Add(Error("CreateObjectFromURL", e));
            }

            // 2. Presentation descriptor: duration, stream descriptors, protection.
            if (source != null)
            {
                try
                {
                    uint ch;
                    source.GetCharacteristics(out ch);
                    root.Add("sourceCharacteristics", "0x" + ch.ToString("X"));
                    source.CreatePresentationDescriptor(out pd);
                    IMFAttributes pda = (IMFAttributes)pd;
                    ulong dur;
                    if (TryUInt64(pda, MF_PD_DURATION, out dur)) root.Add("duration100ns", dur);
                    root.Add("presentationAttributes", DumpAttributes(pda));
                    uint n;
                    pd.GetStreamDescriptorCount(out n);
                    root.Add("streamCount", n);
                    bool anyProtected = false;
                    List<object> streams = new List<object>();
                    for (uint i = 0; i < n; i++)
                    {
                        Obj so = new Obj().Add("index", i);
                        try
                        {
                            bool selected;
                            IMFStreamDescriptor sd;
                            pd.GetStreamDescriptorByIndex(i, out selected, out sd);
                            so.Add("selectedByDefault", selected);
                            uint id;
                            sd.GetStreamIdentifier(out id);
                            streamIds.Add(id);
                            so.Add("streamId", id);
                            IMFAttributes sda = (IMFAttributes)sd;
                            uint prot;
                            bool isProt = TryUInt32(sda, MF_SD_PROTECTED, out prot) && prot != 0;
                            so.Add("protected", isProt);
                            if (isProt) anyProtected = true;
                            try
                            {
                                uint cch;
                                sda.GetStringLength(MF_SD_LANGUAGE, out cch);
                                StringBuilder sb = new StringBuilder((int)cch + 1);
                                sda.GetString(MF_SD_LANGUAGE, sb, cch + 1, out cch);
                                so.Add("language", sb.ToString());
                            }
                            catch (COMException) { }
                            try
                            {
                                uint cch;
                                sda.GetStringLength(MF_SD_STREAM_NAME, out cch);
                                StringBuilder sb = new StringBuilder((int)cch + 1);
                                sda.GetString(MF_SD_STREAM_NAME, sb, cch + 1, out cch);
                                so.Add("streamName", sb.ToString());
                            }
                            catch (COMException) { }
                            so.Add("descriptorAttributes", DumpAttributes(sda));
                            IMFMediaTypeHandler handler;
                            sd.GetMediaTypeHandler(out handler);
                            IMFMediaType cur;
                            handler.GetCurrentMediaType(out cur);
                            so.Add("type", DescribeType(cur));
                            uint tc;
                            handler.GetMediaTypeCount(out tc);
                            so.Add("mediaTypeCount", tc);
                        }
                        catch (Exception e)
                        {
                            so.Add("error", Error("stream " + i, e));
                        }
                        streams.Add(so);
                    }
                    root.Add("streams", streams);
                    root.Add("anyStreamProtected", anyProtected);
                }
                catch (Exception e)
                {
                    errors.Add(Error("presentation descriptor", e));
                }
            }

            // 3. ASF profile from the header, through the documented ASF content-info object.
            try
            {
                if (LooksLikeAsf(path)) root.Add("asfProfile", AsfProfile(path));
            }
            catch (Exception e)
            {
                errors.Add(Error("asf profile", e));
            }

            // 4. Source reader: native types, then the sample stream.
            try
            {
                if (source != null)
                {
                    Check(Native.MFCreateSourceReaderFromMediaSource(source, null, out reader), "MFCreateSourceReaderFromMediaSource");
                }
                else
                {
                    int rhr = Native.MFCreateSourceReaderFromURL(path, null, out reader);
                    root.Add("readerFromUrl", Hex((uint)rhr));
                    if (rhr < 0) { errors.Add(HrError("MFCreateSourceReaderFromURL", rhr)); reader = null; }
                }
            }
            catch (Exception e)
            {
                errors.Add(Error("source reader", e));
                reader = null;
            }

            if (reader != null)
            {
                try
                {
                    root.Add("samples", ReadSamples(reader, firstSamples, errors));
                }
                catch (Exception e)
                {
                    errors.Add(Error("read samples", e));
                }
            }

            root.Add("opened", opened || reader != null);
            root.Add("errors", errors);
            root.Add("probeMilliseconds", total.ElapsedMilliseconds);
            try { if (source != null) source.Shutdown(); } catch (Exception) { }
            File.WriteAllText(outPath, Json.Write(root));
            return 0;
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed", hr);
        }

        static bool LooksLikeAsf(string path)
        {
            // The file starts with the ASF Header Object GUID (30 26 B2 75 ...), as observed in published files.
            byte[] head = new byte[4];
            using (FileStream fs = File.OpenRead(path))
            {
                if (fs.Read(head, 0, 4) < 4) return false;
            }
            return head[0] == 0x30 && head[1] == 0x26 && head[2] == 0xB2 && head[3] == 0x75;
        }

        static IMFMediaBuffer MemoryBuffer(byte[] data, int length)
        {
            IMFMediaBuffer buf;
            Check(Native.MFCreateMemoryBuffer((uint)length, out buf), "MFCreateMemoryBuffer");
            IntPtr p;
            uint max, cur;
            buf.Lock(out p, out max, out cur);
            Marshal.Copy(data, 0, p, length);
            buf.Unlock();
            buf.SetCurrentLength((uint)length);
            return buf;
        }

        static Obj AsfProfile(string path)
        {
            Obj o = new Obj();
            IMFASFContentInfo info;
            Check(Native.MFCreateASFContentInfo(out info), "MFCreateASFContentInfo");
            byte[] first = new byte[30];
            using (FileStream fs = File.OpenRead(path))
            {
                if (fs.Read(first, 0, 30) < 30) throw new IOException("short file");
                IMFMediaBuffer b0 = MemoryBuffer(first, 30);
                ulong headerSize;
                info.GetHeaderSize(b0, out headerSize);
                o.Add("headerSize", headerSize);
                if (headerSize > 64UL * 1024 * 1024) throw new IOException("header too large");
                byte[] header = new byte[(int)headerSize];
                fs.Position = 0;
                int got = 0;
                while (got < header.Length)
                {
                    int r = fs.Read(header, got, header.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                IMFMediaBuffer b1 = MemoryBuffer(header, got);
                info.ParseHeader(b1, 0);
            }
            IMFASFProfile profile;
            info.GetProfile(out profile);
            uint count;
            profile.GetStreamCount(out count);
            o.Add("streamCount", count);
            List<object> streams = new List<object>();
            for (uint i = 0; i < count; i++)
            {
                Obj so = new Obj();
                try
                {
                    ushort number;
                    IMFASFStreamConfig cfg;
                    profile.GetStream(i, out number, out cfg);
                    so.Add("streamNumber", (int)number);
                    Guid type;
                    cfg.GetStreamType(out type);
                    so.Add("streamType", type.ToString("D"));
                    try
                    {
                        IMFMediaType mt;
                        cfg.GetMediaType(out mt);
                        so.Add("type", DescribeType(mt));
                    }
                    catch (Exception e) { so.Add("typeError", Error("GetMediaType", e)); }
                    so.Add("attributes", DumpAttributes((IMFAttributes)cfg));
                }
                catch (Exception e) { so.Add("error", Error("profile stream " + i, e)); }
                streams.Add(so);
            }
            o.Add("streams", streams);
            // Mutually exclusive streams are how multi-bitrate files declare their alternatives.
            uint mc;
            profile.GetMutualExclusionCount(out mc);
            List<object> excl = new List<object>();
            for (uint i = 0; i < mc; i++)
            {
                Obj eo = new Obj();
                try
                {
                    IMFASFMutualExclusion me;
                    profile.GetMutualExclusion(i, out me);
                    Guid mtype;
                    me.GetExclusionType(out mtype);
                    eo.Add("type", mtype.ToString("D"));
                    uint rc;
                    me.GetRecordCount(out rc);
                    List<object> recs = new List<object>();
                    for (uint r = 0; r < rc; r++)
                    {
                        uint n = 0;
                        ushort[] ids = new ushort[256];
                        n = 256;
                        me.GetStreamsForRecord(r, ids, ref n);
                        List<object> l = new List<object>();
                        for (int k = 0; k < n && k < ids.Length; k++) l.Add((int)ids[k]);
                        recs.Add(l);
                    }
                    eo.Add("records", recs);
                }
                catch (Exception e) { eo.Add("error", Error("mutual exclusion " + i, e)); }
                excl.Add(eo);
            }
            o.Add("mutualExclusions", excl);
            return o;
        }

        class StreamCounters
        {
            public long Count, Bytes, FirstTime = long.MinValue, LastTime = long.MinValue, MinTime = long.MaxValue, MaxTime = long.MinValue;
            public long Keyframes, Discontinuities, NoDuration, Ticks, TypeChanges;
            public bool Ended;
            public List<object> First = new List<object>();
        }

        static Obj ReadSamples(IMFSourceReader reader, int firstSamples, List<object> errors)
        {
            Obj o = new Obj();
            // How the reader sees each stream before we change anything.
            List<object> sel = new List<object>();
            List<uint> indexes = new List<uint>();
            int active = 0;
            for (uint i = 0; i < 64; i++)
            {
                bool s;
                try { reader.GetStreamSelection(i, out s); }
                catch (COMException) { break; }
                indexes.Add(i);
                Obj so = new Obj().Add("index", i).Add("selectedByDefault", s);
                try
                {
                    IMFMediaType nt;
                    reader.GetNativeMediaType(i, 0, out nt);
                    so.Add("nativeType", DescribeType(nt));
                }
                catch (Exception e) { so.Add("nativeTypeError", Error("GetNativeMediaType", e)); }
                try
                {
                    reader.SetStreamSelection(i, true);
                    bool after;
                    reader.GetStreamSelection(i, out after);
                    so.Add("selectedAfterRequest", after);
                    if (after) active++;
                }
                catch (Exception e)
                {
                    so.Add("selectError", Error("SetStreamSelection", e));
                    if (s) active++;
                }
                sel.Add(so);
            }
            o.Add("readerStreams", sel);

            Dictionary<uint, StreamCounters> counters = new Dictionary<uint, StreamCounters>();
            foreach (uint i in indexes) counters[i] = new StreamCounters();

            Stopwatch sw = Stopwatch.StartNew();
            long reads = 0;
            string stop = "end-of-stream";
            int ended = 0;
            // 100 ns per tick is Media Foundation's unit; keep it raw so nothing is rounded here.
            long idle = 0;
            while (ended < active)
            {
                // Repeated end-of-stream answers with no sample mean nothing more will come.
                if (idle > 200) { stop = "idle"; break; }
                if (sw.ElapsedMilliseconds > 25000) { stop = "time-cap"; break; }
                if (reads > 3000000) { stop = "read-cap"; break; }
                uint actual, flags;
                long ts;
                IMFSample sample = null;
                try
                {
                    reader.ReadSample(ANY_STREAM, 0, out actual, out flags, out ts, out sample);
                }
                catch (COMException e)
                {
                    errors.Add(Error("ReadSample after " + reads + " reads", e));
                    stop = "error";
                    break;
                }
                reads++;
                StreamCounters c;
                if (!counters.TryGetValue(actual, out c))
                {
                    c = new StreamCounters();
                    counters[actual] = c;
                }
                if ((flags & 0x1) != 0) { errors.Add(new Obj().Add("where", "ReadSample flags").Add("message", "MF_SOURCE_READERF_ERROR on stream " + actual)); stop = "error"; break; }
                if ((flags & 0x100) != 0) c.Ticks++;
                if ((flags & 0x10) != 0) c.TypeChanges++;
                if ((flags & 0x2) != 0 && !c.Ended) { c.Ended = true; ended++; }
                if (sample == null) { idle++; continue; }
                idle = 0;

                long time = ts, dur = -1;
                uint len = 0;
                try { sample.GetSampleTime(out time); } catch (COMException) { }
                try { sample.GetSampleDuration(out dur); } catch (COMException) { c.NoDuration++; }
                try { sample.GetTotalLength(out len); } catch (COMException) { }
                IMFAttributes sa = (IMFAttributes)sample;
                uint clean, disc;
                bool isClean = TryUInt32(sa, MFSampleExtension_CleanPoint, out clean) && clean != 0;
                bool isDisc = TryUInt32(sa, MFSampleExtension_Discontinuity, out disc) && disc != 0;
                if (isClean) c.Keyframes++;
                if (isDisc) c.Discontinuities++;
                if (c.Count == 0) c.FirstTime = time;
                c.LastTime = time;
                if (time < c.MinTime) c.MinTime = time;
                if (time > c.MaxTime) c.MaxTime = time;
                c.Bytes += len;
                if (c.Count < firstSamples)
                {
                    Obj so = new Obj().Add("n", c.Count).Add("time100ns", time);
                    if (dur >= 0) so.Add("duration100ns", dur);
                    so.Add("size", len).Add("cleanPoint", isClean).Add("discontinuity", isDisc);
                    if (len > 0 && len <= MaxSampleBytesHashed)
                    {
                        try
                        {
                            IMFMediaBuffer buf;
                            sample.ConvertToContiguousBuffer(out buf);
                            IntPtr p;
                            uint max, cur;
                            buf.Lock(out p, out max, out cur);
                            byte[] bytes = new byte[cur];
                            Marshal.Copy(p, bytes, 0, (int)cur);
                            buf.Unlock();
                            using (SHA256 h = SHA256.Create())
                                so.Add("sha256", BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
                            so.Add("head", BitConverter.ToString(bytes, 0, (int)Math.Min(16, cur)).Replace("-", ""));
                        }
                        catch (Exception e) { so.Add("bufferError", e.Message.Trim()); }
                    }
                    c.First.Add(so);
                }
                c.Count++;
            }

            List<object> totals = new List<object>();
            foreach (KeyValuePair<uint, StreamCounters> kv in counters)
            {
                StreamCounters c = kv.Value;
                if (c.Count == 0 && c.Ticks == 0 && !c.Ended) continue;
                Obj t = new Obj().Add("readerStream", kv.Key).Add("samples", c.Count).Add("bytes", c.Bytes);
                if (c.Count > 0)
                    t.Add("firstTime100ns", c.FirstTime).Add("lastTime100ns", c.LastTime).Add("minTime100ns", c.MinTime).Add("maxTime100ns", c.MaxTime);
                t.Add("cleanPoints", c.Keyframes).Add("discontinuities", c.Discontinuities).Add("withoutDuration", c.NoDuration)
                 .Add("streamTicks", c.Ticks).Add("nativeTypeChanges", c.TypeChanges).Add("endOfStream", c.Ended);
                t.Add("firstSamples", c.First);
                totals.Add(t);
            }
            o.Add("streamTotals", totals);
            o.Add("reads", reads);
            o.Add("stoppedBy", stop);
            o.Add("readMilliseconds", sw.ElapsedMilliseconds);
            return o;
        }

        // -------- Shell thumbnail

        static int Thumb(string path, string outPath)
        {
            Obj o = new Obj().Add("file", Path.GetFileName(path));
            int code = 0;
            try
            {
                IShellItemImageFactory factory;
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                int hr = Native.SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out factory);
                o.Add("createItem", Hex((uint)hr));
                if (hr >= 0)
                {
                    SIZE size = new SIZE();
                    size.cx = 256; size.cy = 256;
                    IntPtr hbm;
                    // SIIGBF_BIGGERSIZEOK (1) | SIIGBF_THUMBNAILONLY (8): fail rather than fall back to a generic icon.
                    int ghr = factory.GetImage(size, 0x9, out hbm);
                    o.Add("getImage", Hex((uint)ghr));
                    o.Add("thumbnail", ghr >= 0);
                    if (ghr >= 0)
                    {
                        using (Bitmap bmp = Image.FromHbitmap(hbm))
                        {
                            bmp.Save(outPath, ImageFormat.Png);
                            o.Add("width", bmp.Width).Add("height", bmp.Height);
                        }
                        Native.DeleteObject(hbm);
                    }
                }
                else
                {
                    o.Add("thumbnail", false);
                }
            }
            catch (Exception e)
            {
                o.Add("thumbnail", false).Add("error", e.Message.Trim());
                code = 1;
            }
            File.WriteAllText(outPath + ".json", Json.Write(o));
            return code;
        }
    }
}
