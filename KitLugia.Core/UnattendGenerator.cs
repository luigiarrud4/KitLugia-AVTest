using System.IO;
using System.Text;

namespace KitLugia.Core
{
    public static class UnattendGenerator
    {
        public static void Generate(string savePath, string pcName, bool bypassReqs, bool skipOobe)
        {
            StringBuilder xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.AppendLine("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">");
            
            // Bypass TPM/SecureBoot (WinPE Pass)
            if (bypassReqs)
            {
                xml.AppendLine("  <settings pass=\"windowsPE\">");
                xml.AppendLine("    <component name=\"Microsoft-Windows-Setup\" processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\">");
                xml.AppendLine("      <RunSynchronous>");
                xml.AppendLine("        <RunSynchronousCommand wcm:action=\"add\">");
                xml.AppendLine("          <Order>1</Order>");
                xml.AppendLine("          <Path>reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f</Path>");
                xml.AppendLine("        </RunSynchronousCommand>");
                xml.AppendLine("        <RunSynchronousCommand wcm:action=\"add\">");
                xml.AppendLine("          <Order>2</Order>");
                xml.AppendLine("          <Path>reg add HKLM\\SYSTEM\\Setup\\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f</Path>");
                xml.AppendLine("        </RunSynchronousCommand>");
                xml.AppendLine("      </RunSynchronous>");
                xml.AppendLine("      <UserData><AcceptEula>true</AcceptEula></UserData>");
                xml.AppendLine("    </component>");
                xml.AppendLine("  </settings>");
            }

            // Skip OOBE (oobeSystem Pass)
            if (skipOobe)
            {
                xml.AppendLine("  <settings pass=\"oobeSystem\">");
                xml.AppendLine("    <component name=\"Microsoft-Windows-Shell-Setup\" processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\">");
                xml.AppendLine("      <OOBE>");
                xml.AppendLine("        <HideEULAPage>true</HideEULAPage>");
                xml.AppendLine("        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>");
                xml.AppendLine("        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>");
                xml.AppendLine("        <ProtectYourPC>3</ProtectYourPC>");
                xml.AppendLine("      </OOBE>");
                xml.AppendLine($"      <ComputerName>{pcName}</ComputerName>");
                xml.AppendLine("    </component>");
                xml.AppendLine("  </settings>");
            }

            xml.AppendLine("</unattend>");
            File.WriteAllText(savePath, xml.ToString());
        }
    }
}
