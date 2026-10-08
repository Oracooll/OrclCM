// Reads every battery straight from the Windows battery class driver
// (SetupAPI + IOCTL_BATTERY_*), the same source Windows' own battery meter uses.
// No admin rights, no WMI, no child processes.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OrclCM
{
    static class NativeBattery
    {
        static readonly Guid BatteryClass = new Guid("72631E54-78A4-11D0-BCF7-00AA00B7B32A"); // GUID_DEVCLASS_BATTERY
        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10, ERROR_NO_MORE_ITEMS = 259;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ_WRITE = 0x3, OPEN_EXISTING = 3;
        const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044,
                   IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
        const int BatteryInformationLevel = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_INFORMATION
        {
            public uint Capabilities; public byte Technology, Reserved1, Reserved2, Reserved3; public uint Chemistry;
            public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index,
                                                       ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail,
                                                           int detailSize, out int requiredSize, IntPtr devInfo);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
                                                uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint input, int inSize,
                                           out uint output, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION input, int inSize,
                                           out BATTERY_INFORMATION output, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS input, int inSize,
                                           out BATTERY_STATUS output, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        public static Reading Read() => Battery.Aggregate(ReadAll(), AcOnline());

        public static List<BatteryRecord> ReadAll()
        {
            var result = new List<BatteryRecord>();
            Exception firstError = null;
            var guid = BatteryClass;
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1))
                throw new Win32Exception();
            try
            {
                for (int i = 0; i < 64; i++)
                {
                    var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did))
                    {
                        if (Marshal.GetLastWin32Error() == ERROR_NO_MORE_ITEMS) break;
                        throw new Win32Exception();
                    }
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 8) continue;  // no usable device path
                    IntPtr detail = Marshal.AllocHGlobal(size);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on 64-bit, 6 on 32-bit
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, size, out size, IntPtr.Zero))
                            throw new Win32Exception();
                        try
                        {
                            var record = ReadOne(Marshal.PtrToStringUni(IntPtr.Add(detail, 4)));
                            if (record != null) result.Add(record);
                        }
                        catch (Win32Exception e) { firstError = firstError ?? e; }  // one bad device (e.g. a UPS) must not hide the others
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            if (result.Count == 0 && firstError != null) throw firstError;
            return result;
        }

        static BatteryRecord ReadOne(string devicePath)
        {
            using (var h = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero,
                                      OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                    throw new Win32Exception();
                uint wait = 0;
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out uint tag, 4, out _, IntPtr.Zero) || tag == 0)
                    return null;  // empty battery slot

                var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryInformationLevel };
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref query, Marshal.SizeOf(query),
                                     out BATTERY_INFORMATION info, Marshal.SizeOf(typeof(BATTERY_INFORMATION)), out _, IntPtr.Zero))
                    throw new Win32Exception();

                var ws = new BATTERY_WAIT_STATUS { BatteryTag = tag };
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ref ws, Marshal.SizeOf(ws),
                                     out BATTERY_STATUS status, Marshal.SizeOf(typeof(BATTERY_STATUS)), out _, IntPtr.Zero))
                    throw new Win32Exception();

                return new BatteryRecord
                {
                    Capabilities = info.Capabilities,
                    FullChargedCapacity = info.FullChargedCapacity,
                    DesignedCapacity = info.DesignedCapacity,
                    CycleCount = info.CycleCount,
                    PowerState = status.PowerState,
                    Capacity = status.Capacity,
                    Rate = status.Rate,
                };
            }
        }

        static bool? AcOnline()
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s)) return null;
            return s.ACLineStatus == 1 ? true : s.ACLineStatus == 0 ? false : (bool?)null;
        }
    }
}
