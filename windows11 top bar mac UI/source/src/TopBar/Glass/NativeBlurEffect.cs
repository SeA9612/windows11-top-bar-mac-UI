using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;
using WinRT;

namespace GlassDock.TopBar;

// Direct2D effect metadata consumed by Windows Composition. This effect is
// applied only to the compositor's host backdrop source, never WPF content.
[ComVisible(true), ClassInterface(ClassInterfaceType.None), WinRTExposedType(typeof(NativeBlurEffectDetails))]
internal sealed class NativeBlurEffect : IGraphicsEffect, IGraphicsEffectSource, IGraphicsEffectD2D1Interop
{
    public string Name { get; set; } = "DesktopBlur";
    internal IGraphicsEffectSource Source { get; set; } = null!;
    public int GetEffectId(out Guid id) { id = new Guid("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5"); return 0; }
    public int GetNamedPropertyMapping(string name, out uint index, out uint mapping) { index = 0; mapping = 1; return 0; }
    public int GetPropertyCount(out uint count) { count = 3; return 0; }
    public int GetProperty(uint index, out nint value)
    {
        object property = index switch
        {
            0 => PropertyValue.CreateSingle(GlassTheme.NativeBackdropSigma),
            1 => PropertyValue.CreateUInt32(1),
            2 => PropertyValue.CreateUInt32(1),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        nint inspectable = MarshalInspectable<object>.FromManaged(property);
        try
        {
            Guid propertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
            return Marshal.QueryInterface(inspectable, ref propertyValue, out value);
        }
        finally { Marshal.Release(inspectable); }
    }
    public int GetSource(uint index, out nint source) { source = MarshalInterface<IGraphicsEffectSource>.FromManaged(Source); return index == 0 ? 0 : unchecked((int)0x80070057); }
    public int GetSourceCount(out uint count) { count = 1; return 0; }
}

[ComVisible(true), Guid("2FC57384-A068-44D7-A331-30982FCF7177"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsEffectD2D1Interop
{
    [PreserveSig] int GetEffectId(out Guid id);
    [PreserveSig] int GetNamedPropertyMapping([MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, out uint mapping);
    [PreserveSig] int GetPropertyCount(out uint count);
    [PreserveSig] int GetProperty(uint index, out nint value);
    [PreserveSig] int GetSource(uint index, out nint source);
    [PreserveSig] int GetSourceCount(out uint count);
}

// CsWinRT requires an explicit ABI entry for this classic COM interop interface.
// The standard GraphicsEffect/Source ABI entries remain SDK-provided.
internal sealed class NativeBlurEffectDetails : IWinRTExposedTypeDetails
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int IdCall(nint self, out Guid id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NameCall(nint self, nint name, out uint index, out uint mapping);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CountCall(nint self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ValueCall(nint self, uint index, out nint value);

    static readonly IdCall GetId = (nint self, out Guid id) => ComWrappersSupport.FindObject<NativeBlurEffect>(self).GetEffectId(out id);
    static readonly NameCall GetName = (nint self, nint name, out uint index, out uint mapping) => ComWrappersSupport.FindObject<NativeBlurEffect>(self).GetNamedPropertyMapping(Marshal.PtrToStringUni(name) ?? "", out index, out mapping);
    static readonly CountCall PropertyCount = (nint self, out uint count) => ComWrappersSupport.FindObject<NativeBlurEffect>(self).GetPropertyCount(out count);
    static readonly CountCall SourceCount = (nint self, out uint count) => ComWrappersSupport.FindObject<NativeBlurEffect>(self).GetSourceCount(out count);
    static readonly ValueCall Property = ReadProperty;
    static readonly ValueCall Source = ReadSource;
    static readonly nint Vtable = CreateVtable();

    public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces() => new[]
    {
        new ComWrappers.ComInterfaceEntry { IID = typeof(IGraphicsEffect).GUID, Vtable = SdkVtable("IGraphicsEffect") },
        new ComWrappers.ComInterfaceEntry { IID = typeof(IGraphicsEffectSource).GUID, Vtable = SdkVtable("IGraphicsEffectSource") },
        new ComWrappers.ComInterfaceEntry { IID = typeof(IGraphicsEffectD2D1Interop).GUID, Vtable = Vtable }
    };

    static nint SdkVtable(string name) => (nint)typeof(IGraphicsEffect).Assembly.GetType("ABI.Windows.Graphics.Effects." + name)!
        .GetField("AbiToProjectionVftablePtr", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    static nint CreateVtable()
    {
        ComWrappers.GetIUnknownImpl(out nint query, out nint add, out nint release);
        nint table = Marshal.AllocHGlobal(nint.Size * 9);
        nint[] methods = { query, add, release, Marshal.GetFunctionPointerForDelegate(GetId), Marshal.GetFunctionPointerForDelegate(GetName), Marshal.GetFunctionPointerForDelegate(PropertyCount), Marshal.GetFunctionPointerForDelegate(Property), Marshal.GetFunctionPointerForDelegate(Source), Marshal.GetFunctionPointerForDelegate(SourceCount) };
        for (int index = 0; index < methods.Length; index++) Marshal.WriteIntPtr(table, index * nint.Size, methods[index]);
        return table;
    }
    static int ReadProperty(nint self, uint index, out nint value)
    {
        value = 0;
        try { return ComWrappersSupport.FindObject<NativeBlurEffect>(self).GetProperty(index, out value); }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }
    static int ReadSource(nint self, uint index, out nint value)
    {
        value = 0;
        try { return ComWrappersSupport.FindObject<NativeBlurEffect>(self).GetSource(index, out value); }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }
}
