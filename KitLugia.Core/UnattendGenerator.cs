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
            
            // Bypass TPM/SecureBoot + BypassNRO (WinPE Pass — runs early in setup)
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
                xml.AppendLine("        <RunSynchronousCommand wcm:action=\"add\">");
                xml.AppendLine("          <Order>3</Order>");
                xml.AppendLine("          <Path>reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f</Path>");
                xml.AppendLine("        </RunSynchronousCommand>");
                xml.AppendLine("      </RunSynchronous>");
                xml.AppendLine("      <UserData><AcceptEula>true</AcceptEula></UserData>");
                xml.AppendLine("    </component>");
                xml.AppendLine("  </settings>");
            }

            // Skip OOBE (oobeSystem Pass) — cria conta local e pula telas de internet/MSA
            if (skipOobe)
            {
                xml.AppendLine("  <settings pass=\"oobeSystem\">");
                xml.AppendLine("    <component name=\"Microsoft-Windows-Shell-Setup\" processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\">");
                xml.AppendLine("      <UserAccounts>");
                xml.AppendLine("        <LocalAccounts>");
                xml.AppendLine("          <LocalAccount wcm:action=\"add\">");
                xml.AppendLine("            <Password><Value><![CDATA[]]></Value><PlainText>true</PlainText></Password>");
                xml.AppendLine("            <Group>Administrators</Group>");
                xml.AppendLine("            <DisplayName>Usuario</DisplayName>");
                xml.AppendLine("            <Name>Usuario</Name>");
                xml.AppendLine("          </LocalAccount>");
                xml.AppendLine("        </LocalAccounts>");
                xml.AppendLine("      </UserAccounts>");
                xml.AppendLine("      <OOBE>");
                xml.AppendLine("        <HideEULAPage>true</HideEULAPage>");
                xml.AppendLine("        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>");
                xml.AppendLine("        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>");
                xml.AppendLine("        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>");
                xml.AppendLine("        <SkipUserOOBE>true</SkipUserOOBE>");
                xml.AppendLine("        <SkipMachineOOBE>true</SkipMachineOOBE>");
                xml.AppendLine("        <ProtectYourPC>1</ProtectYourPC>");
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
