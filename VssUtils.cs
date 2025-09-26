using System;
using System.Management;

namespace ImagingUtility
{
    internal class VssSnapshotInfo
    {
        public string ShadowId { get; set; } = string.Empty;
        public string? DeviceObject { get; set; }
    }

    internal class VssUtils
    {
        #pragma warning disable CA1416 // Windows-only APIs
        public VssSnapshotInfo CreateSnapshot(string volume)
        {
            // volume should be like C:\
            var scope = new ManagementScope("ROOT\\CIMV2");
            scope.Connect();

            var wmiClass = new ManagementClass(scope, new ManagementPath("Win32_ShadowCopy"), new ObjectGetOptions());
            var inParams = wmiClass.GetMethodParameters("Create");
            inParams["Volume"] = volume;
            inParams["Context"] = "ClientAccessible"; // makes it accessible

            var outParams = wmiClass.InvokeMethod("Create", inParams, new InvokeMethodOptions());
            var returnValue = (uint)outParams.Properties["ReturnValue"].Value;
            if (returnValue != 0)
                throw new InvalidOperationException($"VSS Create failed: {returnValue}");

            var id = outParams.Properties["ShadowID"].Value?.ToString() ?? string.Empty;
            // Query the created shadow to get DeviceObject
            var q = new SelectQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id}'");
            using var searcher = new ManagementObjectSearcher(scope, q);
            foreach (ManagementObject mo in searcher.Get())
            {
                var device = mo["DeviceObject"]?.ToString();
                return new VssSnapshotInfo { ShadowId = id, DeviceObject = device };
            }

            return new VssSnapshotInfo { ShadowId = id };
        }

        public void DeleteSnapshot(string shadowId)
        {
            var scope = new ManagementScope("ROOT\\CIMV2");
            scope.Connect();
            var q = new SelectQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{shadowId}'");
            using var searcher = new ManagementObjectSearcher(scope, q);
            foreach (ManagementObject mo in searcher.Get())
            {
                mo.Delete();
                return;
            }
        }
        #pragma warning restore CA1416
    }
}
