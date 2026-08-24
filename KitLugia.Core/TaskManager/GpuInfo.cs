using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Enumeração UNIVERSAL de adaptadores gráficos via DXGI (dxgi.dll).
    /// Funciona com QUALQUER GPU que tenha driver WDDM: NVIDIA, AMD, Intel,
    /// Moore Threads, Zhaoxin, Matrox, virtual (Hyper-V/WSL DDA) etc.
    /// É a mesma API que o Windows usa para listar adaptadores no dxdiag.
    ///
    /// Prioridade de fontes por campo:
    ///   Nome/VRAM : DXGI Factory → registro HardwareInformation.qwMemorySize → WMI
    ///   Driver    : DXGI (versão completa) ou WMI
    ///   Utilização: PDH "GPU Engine" (já coberto por GpuMonitor)
    /// </summary>
    public static class GpuInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        private static readonly Guid DXGIFactory1Guid = new("770aae78-f26f-4dba-a829-e6e8a5f5ff09");

        // Offsets de vtable COM (métodos na ordem da interface):
        //   IUnknown: 0=QI 1=AddRef 2=Release
        //   IDXGIObject: 3=SetPrivateData 4=SetPrivateDataInterface 5=GetPrivateData 6=GetParent
        //   IDXGIFactory: 7=EnumAdapters 8=MakeWindowAssociation 9=GetWindowAssociation
        //                 10=CreateSwapChain 11=CreateSoftwareAdapter
        //   IDXGIFactory1: 12=EnumAdapters1 13=IsCurrent
        private const int VtEnumAdapters1 = 12;
        // IDXGIAdapter: 3=GetDesc 4=CheckInterfaceSupport 5=EnumOutputs
        // IDXGIAdapter1: 6=GetDesc1
        private const int VtGetDesc1 = 6;

        private delegate int EnumAdapters1Delegate(IntPtr self, uint index, out IntPtr adapter);
        private delegate int GetDesc1Delegate(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

        private static T GetVtableMethod<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comObject);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
        }

        /// <summary>Lista todas as GPUs do sistema (universal). Nunca lança.</summary>
        public static List<GpuAdapter> GetAll()
        {
            var list = new List<GpuAdapter>();
            try
            {
                Guid iid = DXGIFactory1Guid;
                int hr = CreateDXGIFactory1(ref iid, out IntPtr factory);
                if (hr != 0 || factory == IntPtr.Zero) return list;
                try
                {
                    var enumFn = GetVtableMethod<EnumAdapters1Delegate>(factory, VtEnumAdapters1);
                    for (uint i = 0; ; i++)
                    {
                        if (enumFn(factory, i, out IntPtr adapter) != 0 || adapter == IntPtr.Zero) break;
                        try
                        {
                            var getDesc = GetVtableMethod<GetDesc1Delegate>(adapter, VtGetDesc1);
                            if (getDesc(adapter, out var desc) == 0)
                            {
                                list.Add(new GpuAdapter
                                {
                                    Name = desc.Description ?? "GPU",
                                    VendorId = desc.VendorId,
                                    DeviceId = desc.DeviceId,
                                    DedicatedVideoMemory = (ulong)desc.DedicatedVideoMemory,
                                    SharedSystemMemory = (ulong)desc.SharedSystemMemory,
                                });
                            }
                        }
                        catch { }
                        finally { try { Marshal.Release(adapter); } catch { } }
                    }
                }
                finally { try { Marshal.Release(factory); } catch { } }
            }
            catch { }
            return list;
        }

        /// <summary>Tenta VRAM real via registro (chave de classe display, fonte do dxdiag).</summary>
        public static ulong TryGetVramFromRegistry(string pnpDeviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(pnpDeviceId)) return 0;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (key == null) return 0;
                foreach (var sub in key.GetSubKeyNames())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(sub, @"^00\d\d$")) continue;
                    using var k = key.OpenSubKey(sub);
                    if (k == null) continue;
                    string pnpReg = k.GetValue("MatchingDeviceId")?.ToString() ?? "";
                    if (pnpReg.Length == 0) continue;
                    if (!pnpReg.Contains(pnpDeviceId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                        && !pnpDeviceId.Contains(pnpReg.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    object qw = k.GetValue("HardwareInformation.qwMemorySize");
                    object dw = k.GetValue("HardwareInformation.MemorySize");
                    if (qw is long l && l > 0) return (ulong)l;
                    if (qw is int i2 && i2 > 0) return (ulong)i2;
                    if (dw is long l2 && l2 > 1024 * 1024) return (ulong)l2;
                    if (dw is int d1 && d1 > 1024 * 1024) return (ulong)d1;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Nome amigável do fabricante pelo VendorId PCI SIG.</summary>
        public static string VendorName(uint vendorId) => vendorId switch
        {
            0x10DE => "NVIDIA",
            0x1002 or 0x1022 => "AMD",
            0x8086 => "Intel",
            0x1A03 => "ASPEED",
            0x15AD => "VMware",
            0x1AF4 => "Red Hat virtio",
            0x1234 => "QEMU/Bochs",
            0x1414 => "Microsoft Basic Render",
            _ => $"VEN {vendorId:X4}",
        };
    }

    public sealed class GpuAdapter
    {
        public string Name { get; init; } = "";
        public uint VendorId { get; init; }
        public uint DeviceId { get; init; }
        public ulong DedicatedVideoMemory { get; init; }
        public ulong SharedSystemMemory { get; init; }
    }
}
