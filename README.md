# KitLugia

> Ferramentas para manutenção, diagnóstico, otimização e recuperação do Windows — com interface WPF, núcleo em .NET e rotinas nativas em Rust.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Rust](https://img.shields.io/badge/Rust-native%20module-000000?logo=rust&logoColor=white)](https://www.rust-lang.org/)
[![License](https://img.shields.io/badge/license-MIT-2ea44f.svg)](LICENSE)

O KitLugia é uma aplicação desktop que reúne, em uma única interface, operações que normalmente ficam espalhadas pelo Registro, PowerShell, ferramentas administrativas e utilitários de recuperação do Windows.

Ele não é apenas um painel de botões: dependendo da função escolhida, pode ler e alterar configurações do sistema, remover arquivos e pacotes, executar ferramentas nativas, reconfigurar a rede, preparar discos ou criar mídia de boot.

> **Status:** projeto em desenvolvimento ativo. Algumas áreas são experimentais e podem exigir privilégios de administrador.

## Aviso de segurança

O KitLugia pode alterar o Registro, serviços, plano de energia, configurações de rede, componentes de boot, arquivos do sistema, partições e aplicativos instalados.

Antes de usar funções de alteração:

- crie um ponto de restauração e mantenha um backup;
- confirme o disco, partição, adaptador ou pacote selecionado;
- teste em uma máquina de desenvolvimento antes de usar em produção;
- esteja preparado para reiniciar o Windows ou usar uma mídia de recuperação;
- leia o resultado da operação: algumas mudanças não são totalmente reversíveis.

As classificações abaixo são uma orientação de risco, não uma garantia de segurança:

| Classificação | Significado |
| --- | --- |
| **Baixo** | Leituras, diagnósticos e operações geralmente reversíveis. |
| **Médio** | Altera configurações do sistema, rede, serviços ou arquivos de cache. Pode exigir reinicialização ou ajuste manual. |
| **Alto** | Remove pacotes, altera segurança/boot, reconfigura componentes ou escreve em mídia física. Faça backup antes. |

## O que ele realmente faz

### 1. Otimização do sistema — risco médio

A área de otimização não apenas “melhora o PC” de forma genérica. Ela aplica conjuntos de ajustes escolhidos pelo usuário e pode:

- escrever valores no Registro em escopos de usuário e máquina;
- alterar plano de energia e configurações de desempenho com APIs do Windows;
- configurar opções relacionadas a CPU, GPU, VRAM, agendamento e efeitos visuais;
- ajustar prioridade de processos e tarefas agendadas;
- controlar serviços do Windows e opções de inicialização;
- desativar economias de energia USB ou aplicar ajustes voltados para jogos e latência;
- reverter o conjunto de otimizações quando existe uma ação de restauração correspondente.

O código usa, entre outros mecanismos, Registro, WMI, PowerShell, powercfg, sc.exe e schtasks.exe. O resultado depende do hardware, da versão do Windows e dos drivers: um ajuste que ajuda em um computador pode não ajudar em outro.

Arquivos centrais: SystemTweaks.cs, OptimizationOrchestrator.cs, PowerPlanManager.cs e AdvancedTweaksManager.cs.

### 2. Guardian: diagnóstico de segurança e integridade — leitura e reparos de risco médio/alto

O Guardian é um verificador de configurações do Windows, não um antivírus. Ele procura sinais de configuração frágil ou inconsistente, como:

- estado de mitigações de CPU, CFG, DEP e UAC;
- SMBv1, AutoRun e proteções relacionadas ao kernel;
- configurações de serviços e do BCD, o banco de dados de boot;
- problemas na variável PATH e na integridade do shell do Explorer;
- chaves de Registro e opções que podem reduzir a proteção do sistema.

Além de diagnosticar, algumas rotinas podem reparar configurações, alterar serviços com sc.exe, atualizar opções de boot com bcdedit e assumir propriedade de chaves protegidas quando necessário.

As referências de CVE exibidas pelo projeto ajudam a contextualizar achados; elas não significam que o programa faça uma validação completa de vulnerabilidade nem substituem Windows Update, EDR ou um scanner de segurança.

Arquivo central: Guardian.cs.

### 3. Limpeza e manutenção — risco médio

A limpeza remove resíduos específicos para liberar espaço ou resolver problemas comuns. Entre os alvos implementados estão:

- pastas temporárias do usuário e do Windows;
- cache do Windows Update;
- caches de shaders da GPU;
- logs e arquivos de diagnóstico;
- cache DNS;
- Prefetch;
- Lixeira;
- arquivos antigos encontrados dentro de diretórios selecionados;
- limpeza de pacotes e sobras de aplicativos em operações específicas;
- CompactOS, para compactação de arquivos do sistema.

O código tenta ignorar arquivos bloqueados ou sem permissão e registra o que não conseguiu remover. Ainda assim, apagar caches e pastas de sistema pode aumentar temporariamente o tempo de inicialização de alguns aplicativos ou exigir privilégios elevados.

Arquivo central: CleanupManager.cs.

### 4. Aplicativos pré-instalados e bloatware — risco alto

O gerenciador de bloatware lista pacotes instalados, permite selecionar pacotes para remoção e pode encaminhar o usuário à Microsoft Store para reinstalação de aplicativos compatíveis.

A remoção opera sobre os nomes dos pacotes e pode usar padrões. Por isso, uma seleção ampla demais pode remover componentes que o usuário pretendia manter ou afetar outros perfis do Windows.

Arquivos centrais: BloatwareManager.cs e SystemTweaks.cs.

### 5. Ferramentas de rede — risco médio

A área de rede faz mudanças concretas na configuração do Windows, não apenas um teste de velocidade. Ela pode:

- trocar o DNS para perfis como Cloudflare, Google ou DHCP;
- ajustar TCP/IP, CTCP, RSS e opções de offload;
- desativar ou alterar componentes de otimização de rede;
- executar diagnósticos de adaptador e conectividade;
- limpar ou redefinir Winsock, TCP/IP e ARP;
- consultar chaves e configurações dos adaptadores de rede;
- medir latência e auxiliar na investigação de conexões instáveis.

As rotinas utilizam netsh, ipconfig, cmdkey, certutil e APIs do Windows. Um reset de rede pode apagar configurações personalizadas, desconectar o computador e exigir reinicialização. Ajustes voltados para latência também podem reduzir estabilidade ou throughput em determinados equipamentos.

Arquivos centrais: NetworkManager.cs, AdapterManager.cs, DnsBenchmark.cs e LatencyAnalyzer.cs.

### 6. Reparos do Windows — risco médio/alto

A área de reparo funciona como uma interface assistida para ferramentas nativas do Windows. Ela pode executar:

- SFC, para verificar e restaurar arquivos protegidos do sistema;
- DISM, para reparar o armazenamento de componentes e a imagem do Windows;
- correções relacionadas ao Windows Update;
- reparos gerais de componentes, serviços e boot.

SFC e DISM podem consumir bastante CPU, disco e tempo. O resultado depende do estado da imagem do Windows e das fontes de reparo disponíveis. O KitLugia executa e apresenta essas operações; ele não consegue garantir que todo dano do sistema será corrigido.

Arquivos centrais: SystemRepair.cs, GeneralRepairManager.cs e WindowsUpdateManager.cs.

### 7. Boot, partições e mídia inicializável — risco alto

Esta é uma das áreas mais sensíveis do projeto. As ferramentas podem:

- enumerar unidades USB e discos disponíveis;
- preparar uma unidade com FAT32 ou NTFS;
- escolher esquema MBR ou GPT;
- criar mídia para instalação do Windows, WinPE, Linux ou configurações dual boot;
- escrever imagem em modo raw/DD;
- editar ou montar imagens ISO;
- atualizar arquivos e configurações de boot;
- trabalhar com BCD, bcdboot, bootsect e diskpart.

O código acessa dispositivos físicos com APIs como CreateFile e DeviceIoControl e executa diskpart para operações de disco. Uma seleção incorreta pode limpar partições e causar perda permanente de dados. Confirme sempre a unidade antes de formatar ou gravar uma imagem.

Arquivos centrais: BootableMediaManager.cs, BootloaderPackager.cs, BootOptimizerManager.cs, PartitionManager.cs, IsoManager.cs e IsoEditorManager.cs.

### 8. WinPE personalizado — risco alto e dependências externas

O construtor de WinPE prepara uma imagem de recuperação personalizada. O fluxo pode:

- baixar ou receber uma base de WinPE;
- criar diretórios temporários de trabalho;
- montar e modificar imagens WIM;
- injetar drivers;
- alterar o startnet.cmd e outros arquivos de inicialização;
- empacotar a imagem em uma ISO final;
- limpar e desmontar os pontos de montagem.

Dependendo do fluxo, ele procura ferramentas como dism.exe, 7z.exe, oscdimg.exe e wimlib-imagex.exe. Se uma montagem não for desmontada corretamente, arquivos e recursos podem permanecer bloqueados até uma limpeza manual ou reinicialização.

Arquivo central: WinpeBuilder.cs.

### 9. Biblioteca nativa em Rust — aceleração de tarefas específicas

O módulo Rust não é uma segunda aplicação completa. Ele fornece rotinas nativas para tarefas específicas, como:

- comparação de strings usando uma implementação de Sift4;
- hashing SHA-256 e BLAKE3;
- análise de caminhos e padrões de arquivos;
- leitura e enumeração de valores do Registro por APIs Win32;
- buscas e verificações com menos sobrecarga que uma implementação puramente gerenciada em alguns cenários.

A biblioteca é compilada como DLL cdylib e integrada ao build .NET quando o artefato nativo está disponível. Ela usa FFI e blocos unsafe para conversar com APIs do Windows, então deve ser compilada e testada para a arquitetura alvo.

Arquivo central: rust_native/src/lib.rs.

## Outras áreas disponíveis na interface

Além dos módulos descritos acima, a solução contém páginas e serviços para drivers, processos, inicialização, tela, privacidade, serviços, programas, atualizações, configurações do Windows, memória, túneis, adaptadores virtuais, WinRE e ferramentas avançadas.

A disponibilidade de cada ação pode variar conforme a versão do Windows, permissões, hardware, drivers e ferramentas externas instaladas.

## O que o KitLugia não é

- não é antivírus nem EDR;
- não é uma garantia automática de aumento de FPS ou redução de latência;
- não substitui backup, ponto de restauração ou mídia de recuperação;
- não torna seguros todos os ajustes só porque eles aparecem na interface;
- não corrige qualquer corrupção do Windows sem depender da imagem e das ferramentas do próprio sistema;
- não é totalmente portátil entre versões e edições do Windows.

## Tecnologia

| Camada | Tecnologia |
| --- | --- |
| Interface | C# + WPF |
| Runtime | .NET 10, SDK definido em global.json |
| Núcleo | KitLugia.Core |
| Sistema | Registro, WMI, APIs Win32 e ferramentas administrativas do Windows |
| Integração nativa | Rust compilado como DLL cdylib |
| Solução | KitLugia.sln |
| Licença | MIT |

## Requisitos

- Windows 10 versão 1903 ou superior, ou Windows 11;
- [.NET SDK 10.0.301](https://dotnet.microsoft.com/download/dotnet/10.0) ou versão compatível com o global.json;
- Git;
- privilégios de administrador para funções que alteram o sistema;
- Rustup e toolchain MSVC para compilar o módulo nativo;
- Visual Studio 2022 ou Build Tools com ferramentas C++ para cenários que dependam da toolchain nativa;
- ferramentas externas como 7-Zip, oscdimg ou wimlib quando o fluxo de WinPE exigir.

## Instalação e execução

~~~powershell
git clone https://github.com/luigiarrud4/KitLugia-AVTest.git
Set-Location KitLugia-AVTest

dotnet restore KitLugia.sln
dotnet build KitLugia.sln --configuration Release
dotnet run --project .\\KitLugia.GUI\\KitLugia.GUI.csproj --configuration Release
~~~

Execute como administrador somente quando a operação escolhida exigir. Evite executar diretamente em um sistema sem backup.

### Compilar o módulo Rust

~~~powershell
cargo build --manifest-path .\\rust_native\\Cargo.toml --release
~~~

Depois, compile a solução .NET novamente para que a DLL nativa seja copiada para a saída quando estiver disponível.

### Publicar para Windows x64

~~~powershell
dotnet publish .\\KitLugia.GUI\\KitLugia.GUI.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true
~~~

O fluxo automatizado de empacotamento está documentado em [DEPLOY.md](DEPLOY.md) e no script [Deploy.ps1](Deploy.ps1).

## Estrutura do repositório

~~~text
KitLugia-AVTest/
├── KitLugia.Core/        # Núcleo: operações, diagnósticos e integrações do Windows
├── KitLugia.GUI/         # Interface WPF, páginas, controles e temas
├── KitLugia.Updater/     # Aplicação de atualização
├── rust_native/          # Biblioteca nativa Rust
├── WinPE_ISO/            # Recursos relacionados a WinPE e ISO
├── docs/                 # Documentação complementar
├── scripts/              # Scripts e artefatos auxiliares
├── Deploy.ps1            # Build, empacotamento e hash SHA-256
├── deploy.bat            # Atalho de deploy no Windows
├── DEPLOY.md             # Publicação e assets de release
├── KitLugia.sln          # Solução principal .NET
└── global.json           # Versão do SDK .NET
~~~

## Contribuindo

Antes de abrir um pull request:

1. teste as alterações em uma instalação de desenvolvimento do Windows;
2. explique quais arquivos, chaves, serviços ou ferramentas externas são envolvidos;
3. informe se a mudança exige administrador, reinicialização ou mídia de recuperação;
4. descreva como reverter a alteração quando ela tocar Registro, boot, rede ou segurança;
5. não inclua binários gerados, pastas bin, obj ou artefatos locais.

## Documentação relacionada

- [DEPLOY.md](DEPLOY.md) — build, empacotamento e publicação;
- [AGENTS.md](AGENTS.md) — contexto e orientações de desenvolvimento;
- [ROADMAP_REVO.md](ROADMAP_REVO.md) — direção do projeto;
- [EXM_TWEAKS_REFERENCE.md](EXM_TWEAKS_REFERENCE.md) — referência de ajustes;
- [LICENSE](LICENSE) — licença MIT.

## Licença

Distribuído sob a licença MIT. Consulte [LICENSE](LICENSE) para o texto completo.

## Autor

Desenvolvido por [Luigi Arruda](https://github.com/luigiarrud4).
