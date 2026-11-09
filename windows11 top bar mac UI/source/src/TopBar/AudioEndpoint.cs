using System;
using System.Runtime.InteropServices;
namespace GlassDock.TopBar;
internal sealed class AudioEndpoint : IDisposable
{
    readonly IEndpointVolume volume;
    bool disposed;
    AudioEndpoint(IEndpointVolume volume){this.volume=volume;}
    public static AudioEndpoint? Open()
    {
        IDeviceEnumerator? enumerator=null;IDevice? device=null;
        try
        {
            enumerator=(IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0,1,out device));
            var iid=typeof(IEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid,23,0,out object endpoint));
            return new((IEndpointVolume)endpoint);
        }
        catch(COMException){return null;}
        finally{if(device!=null)Marshal.ReleaseComObject(device);if(enumerator!=null)Marshal.ReleaseComObject(enumerator);}
    }
    public (float Volume,bool Muted) Read(){Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float scalar));Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));return(scalar,muted);}
    public void SetVolume(float value){var context=Guid.Empty;Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(Math.Clamp(value,0,1),ref context));}
    public void SetMute(bool value){var context=Guid.Empty;Marshal.ThrowExceptionForHR(volume.SetMute(value,ref context));}
    public void Dispose(){if(disposed)return;disposed=true;Marshal.ReleaseComObject(volume);}
}
[ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceEnumerator
{
    [PreserveSig]int EnumAudioEndpoints(int flow,uint state,out nint devices);
    [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IDevice device);
    [PreserveSig]int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IDevice device);
    [PreserveSig]int RegisterEndpointNotificationCallback(nint callback);
    [PreserveSig]int UnregisterEndpointNotificationCallback(nint callback);
}
[ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDevice
{
    [PreserveSig]int Activate(ref Guid iid,uint context,nint parameters,[MarshalAs(UnmanagedType.IUnknown)]out object result);
    [PreserveSig]int OpenPropertyStore(uint mode,out nint store);
    [PreserveSig]int GetId(out nint id);
    [PreserveSig]int GetState(out uint state);
}
[ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEndpointVolume
{
    [PreserveSig]int RegisterControlChangeNotify(nint notify);
    [PreserveSig]int UnregisterControlChangeNotify(nint notify);
    [PreserveSig]int GetChannelCount(out uint count);
    [PreserveSig]int SetMasterVolumeLevel(float level,ref Guid context);
    [PreserveSig]int SetMasterVolumeLevelScalar(float level,ref Guid context);
    [PreserveSig]int GetMasterVolumeLevel(out float level);
    [PreserveSig]int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig]int SetChannelVolumeLevel(uint channel,float level,ref Guid context);
    [PreserveSig]int SetChannelVolumeLevelScalar(uint channel,float level,ref Guid context);
    [PreserveSig]int GetChannelVolumeLevel(uint channel,out float level);
    [PreserveSig]int GetChannelVolumeLevelScalar(uint channel,out float level);
    [PreserveSig]int SetMute([MarshalAs(UnmanagedType.Bool)]bool muted,ref Guid context);
    [PreserveSig]int GetMute([MarshalAs(UnmanagedType.Bool)]out bool muted);
    [PreserveSig]int GetVolumeStepInfo(out uint step,out uint count);
    [PreserveSig]int VolumeStepUp(ref Guid context);
    [PreserveSig]int VolumeStepDown(ref Guid context);
    [PreserveSig]int QueryHardwareSupport(out uint mask);
    [PreserveSig]int GetVolumeRange(out float min,out float max,out float step);
}
