using System;
using System.Runtime.InteropServices;

internal static class DisplayStateProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct SourceInfo { public LUID Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct TargetInfo
    { public LUID Adapter; public uint Id, ModeIndex, OutputTech, Rotation, Scaling; public Rational Refresh; public uint ScanLine; public int Available; public uint Status; }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public LUID Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedInfo { public Header Header; public uint Value, Encoding, BitsPerChannel; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedInfo2 { public Header Header; public uint Value, Encoding, BitsPerChannel, ActiveColorMode; }
    [StructLayout(LayoutKind.Sequential)] private struct WhiteLevelInfo { public Header Header; public uint WhiteLevel; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Name; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Device; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct VersionInfo { public uint Size, Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Csd; }
    private delegate bool MonitorProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathsCount, [Out] PathInfo[] paths, ref uint modesCount, IntPtr modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetAdvanced(ref AdvancedInfo info);
    [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetAdvanced2(ref AdvancedInfo2 info);
    [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetWhite(ref WhiteLevelInfo info);
    [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] private static extern int GetName(ref SourceName info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("ntdll.dll", CharSet=CharSet.Unicode)] private static extern int RtlGetVersion(ref VersionInfo info);

    private static int Main()
    {
        VersionInfo version = new VersionInfo(); version.Size=(uint)Marshal.SizeOf(typeof(VersionInfo));
        int versionResult=RtlGetVersion(ref version);
        Console.WriteLine("OS RtlGetVersion: " + versionResult + " " + version.Major + "." + version.Minor + "." + version.Build);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data)
        {
            MonitorInfo info=new MonitorInfo();info.Size=(uint)Marshal.SizeOf(typeof(MonitorInfo));
            if(GetMonitorInfo(monitor,ref info)) Console.WriteLine("Monitor " + info.Device + " handle=" + monitor.ToInt64() + " bounds=" + info.Monitor.Left + "," + info.Monitor.Top + "," + (info.Monitor.Right-info.Monitor.Left) + "," + (info.Monitor.Bottom-info.Monitor.Top));
            return true;
        },IntPtr.Zero);
        uint pathCount,modeCount;
        int result=GetDisplayConfigBufferSizes(2,out pathCount,out modeCount);
        Console.WriteLine("GetDisplayConfigBufferSizes=" + result + " paths=" + pathCount + " modes=" + modeCount);
        if(result!=0)return 1;
        PathInfo[] paths=new PathInfo[pathCount];
        IntPtr modeData=Marshal.AllocHGlobal(checked((int)modeCount*64));
        try
        {
            result=QueryDisplayConfig(2,ref pathCount,paths,ref modeCount,modeData,IntPtr.Zero);
            Console.WriteLine("QueryDisplayConfig=" + result + " active=" + pathCount + " pathStructSize=" + Marshal.SizeOf(typeof(PathInfo)));
            if(result!=0)return 2;
            for(int i=0;i<pathCount;i++)
            {
                PathInfo path=paths[i];
                SourceName name=new SourceName();name.Header=MakeHeader(1,typeof(SourceName),path.Source.Adapter,path.Source.Id);
                int nameResult=GetName(ref name);
                AdvancedInfo advanced=new AdvancedInfo();advanced.Header=MakeHeader(9,typeof(AdvancedInfo),path.Target.Adapter,path.Target.Id);
                int advancedResult=GetAdvanced(ref advanced);
                AdvancedInfo2 advanced2=new AdvancedInfo2();advanced2.Header=MakeHeader(15,typeof(AdvancedInfo2),path.Target.Adapter,path.Target.Id);
                int advanced2Result=GetAdvanced2(ref advanced2);
                WhiteLevelInfo white=new WhiteLevelInfo();white.Header=MakeHeader(11,typeof(WhiteLevelInfo),path.Target.Adapter,path.Target.Id);
                int whiteResult=GetWhite(ref white);
                Console.WriteLine("Path " + i + " source=" + name.Name + " nameResult=" + nameResult + " targetId=" + path.Target.Id + " outputTech=" + path.Target.OutputTech);
                Console.WriteLine("  AdvancedColor result=" + advancedResult + " raw=0x" + advanced.Value.ToString("X") + " supported=" + ((advanced.Value&1)!=0) + " enabled=" + ((advanced.Value&2)!=0) + " forceDisabled=" + ((advanced.Value&8)!=0) + " encoding=" + advanced.Encoding + " bitsPerChannel=" + advanced.BitsPerChannel);
                Console.WriteLine("  AdvancedColor2 result=" + advanced2Result + " raw=0x" + advanced2.Value.ToString("X") + " activeMode=" + advanced2.ActiveColorMode + " (SDR0/WCG1/HDR2) HDRSupported=" + ((advanced2.Value&16)!=0) + " HDRUserEnabled=" + ((advanced2.Value&32)!=0) + " WCGSupported=" + ((advanced2.Value&64)!=0) + " WCGUserEnabled=" + ((advanced2.Value&128)!=0));
                Console.WriteLine("  SDRWhiteLevel result=" + whiteResult + " raw=" + white.WhiteLevel + " nits=" + (white.WhiteLevel*80.0/1000.0));
            }
        }
        finally { Marshal.FreeHGlobal(modeData); }
        return 0;
    }

    private static Header MakeHeader(uint type,Type size,LUID adapter,uint id)
    { return new Header { Type=type,Size=(uint)Marshal.SizeOf(size),Adapter=adapter,Id=id }; }
}
