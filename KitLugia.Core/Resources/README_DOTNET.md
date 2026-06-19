# Instalador Offline do .NET Desktop Runtime 8.0

## O que é este arquivo?

Este arquivo é usado pelo Winboot para instalar automaticamente o .NET Desktop Runtime 8.0 quando o Windows é inicializado pela primeira vez após a instalação.

## Download Automático 🔥

**O KitLugia baixa automaticamente este arquivo da internet quando você cria um Winboot!**

- No overlay de configuração do Winboot, há um checkbox "📥 Baixar .NET Desktop Runtime 8.0 automaticamente"
- Se marcado (padrão): O KitLugia baixará automaticamente se o arquivo não existir localmente
- URL de download: `https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.15/windowsdesktop-runtime-8.0.15-win-x64.exe`
- O arquivo baixado é salvo nesta pasta para uso futuro
- Progresso do download é mostrado no log
- **Se desmarcado**: O Winboot usará winget na primeira inicialização (requer internet)
- **Falhas no download não interrompem o processo** - o Winboot prossegue normalmente

## Download Manual (Opcional)

Se você preferir baixar manualmente ou se o download automático falhar:

1. Acesse: https://dotnet.microsoft.com/download/dotnet/8.0
2. Em ".NET Desktop Runtime", selecione "x64" para Windows
3. Baixe o arquivo `windowsdesktop-runtime-8.0.x-win-x64.exe` (onde x é a versão mais recente)
4. Renomeie o arquivo baixado para `dotnet-runtime.exe`
5. Substitua o arquivo nesta pasta pelo arquivo baixado

## Versão recomendada

- **.NET Desktop Runtime 8.0.15** (LTS)
- Link direto: https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.15/windowsdesktop-runtime-8.0.15-win-x64.exe

## Por que isso é necessário?

O KitLugia é construído com .NET 8.0. Quando o Windows é instalado via Winboot, o .NET Runtime pode não estar presente. Este instalador garante que o KitLugia possa ser executado imediatamente após a primeira inicialização, sem precisar baixar o runtime da internet.

## Tamanho do arquivo

- Aproximadamente 50-60 MB

## O que acontece se não tiver o instalador?

O script first_logon.bat tentará instalar o .NET via winget (Windows Package Manager), mas isso:
- Requer conexão com internet na primeira inicialização
- Pode falhar se o winget não estiver configurado
- É mais lento que o instalador offline

## Nota para desenvolvedores

Este arquivo é copiado automaticamente para a partição Winboot durante a criação do Winboot. O script first_logon.bat verificará se o .NET está instalado e, se não estiver, executará este instalador silenciosamente.
