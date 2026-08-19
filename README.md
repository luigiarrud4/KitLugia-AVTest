# KitLugia

> Suíte de manutenção, diagnóstico, otimização e recuperação para Windows, construída com .NET, WPF e uma biblioteca nativa em Rust.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![Rust](https://img.shields.io/badge/Rust-native%20module-000000?logo=rust&logoColor=white)](https://www.rust-lang.org/)
[![License](https://img.shields.io/badge/license-MIT-2ea44f.svg)](LICENSE)

O **KitLugia** reúne em uma única interface ferramentas para cuidar do sistema, investigar problemas, preparar mídias de boot, ajustar desempenho e executar reparos do Windows. O projeto combina uma interface desktop em WPF, um núcleo de serviços e uma biblioteca nativa em Rust para tarefas específicas de desempenho e análise.

> **Status:** projeto em desenvolvimento ativo. Algumas funções alteram configurações sensíveis do Windows e podem exigir privilégios de administrador.

## Aviso importante

O KitLugia pode alterar o Registro, serviços, plano de energia, configurações de rede, componentes de boot, arquivos do sistema e aplicativos instalados. Use as ferramentas somente se você souber o efeito da operação:

- crie um ponto de restauração e mantenha um backup antes de aplicar mudanças;
- revise cada ação antes de confirmá-la;
- não execute o programa em máquinas de produção sem validar o comportamento;
- algumas operações podem exigir reinicialização ou mídia de recuperação;
- o projeto não substitui uma solução corporativa de backup, segurança ou gerenciamento de endpoints.

## O que o KitLugia oferece

### Otimização do sistema

- Ajustes de Registro voltados para desempenho e responsividade;
- gerenciamento de planos de energia, incluindo perfis de alto desempenho;
- configuração de efeitos visuais e opções de inicialização/desligamento;
- ajustes relacionados a GPU e agendamento acelerado por hardware;
- gerenciamento de prioridade de processos para CPU, I/O e memória;
- otimizações de memória e de rede usando APIs nativas do Windows.

### Diagnóstico e segurança

O módulo **Guardian** reúne verificações de configuração e integridade, incluindo:

- mitigações de CPU, CFG, DEP e UAC;
- SMBv1, AutoRun e proteções do kernel;
- análise das variáveis de ambiente e integridade do shell do Explorer;
- diagnósticos de configurações de segurança potencialmente frágeis;
- referências de vulnerabilidades para apoiar a investigação e a atualização do sistema.

Os resultados são indicações para investigação, não um certificado de segurança nem um substituto para um scanner profissional.

### Limpeza e manutenção

- remoção de aplicativos pré-instalados e bloatware;
- limpeza de arquivos temporários;
- análise de espaço em disco;
- busca por arquivos duplicados;
- ferramentas de limpeza e manutenção do Registro.

### Boot, recuperação e WinPE

- criação de mídias inicializáveis;
- preparação de USB bootável;
- edição de imagens ISO;
- integração com ferramentas como Rufus e Easy2Boot;
- otimização e recuperação do processo de boot.

### Rede

- gerenciamento de DNS com perfis conhecidos e DHCP;
- ajustes de TCP/IP e controle de congestionamento;
- diagnóstico de conectividade;
- análise de latência e testes de conexão;
- ferramentas para investigar problemas de rede.

### Reparos do Windows

- correções relacionadas ao Windows Update;
- execução assistida de SFC e DISM;
- reparos de componentes;
- recuperação do boot;
- configuração e diagnóstico de serviços.

## Stack tecnológica

| Camada | Tecnologia |
| --- | --- |
| Interface | C# + WPF |
| Runtime | .NET 10 |
| Núcleo | Projeto compartilhado KitLugia.Core |
| Sistema | APIs nativas do Windows, Registro e WMI |
| Integração nativa | Rust como DLL cdylib |
| Solução | KitLugia.sln |
| Licença | MIT |

O módulo Rust fica em rust_native e é configurado para gerar uma biblioteca dinâmica otimizada para release. Quando a DLL está disponível, os projetos .NET a copiam para a saída e para a publicação da aplicação.

## Requisitos

### Obrigatórios

- Windows 10 versão 1903 ou superior, ou Windows 11;
- [.NET SDK 10.0.301](https://dotnet.microsoft.com/download/dotnet/10.0) ou compatível com o global.json;
- Git;
- permissões de administrador para as funções que alteram o sistema.

### Para compilar o módulo Rust

- [Rustup](https://rustup.rs/);
- toolchain MSVC do Rust;
- Visual Studio 2022 com as ferramentas de desenvolvimento para C++ ou o Build Tools equivalente.

### Opcional

- Visual Studio 2022 para desenvolvimento WPF;
- GitHub CLI (gh) para publicar assets de release usando o fluxo em DEPLOY.md.

## Começando

Clone o repositório correto e entre na pasta do projeto:

~~~powershell
git clone https://github.com/luigiarrud4/KitLugia-AVTest.git
Set-Location KitLugia-AVTest
~~~

### Compilar a solução

~~~powershell
dotnet restore KitLugia.sln
dotnet build KitLugia.sln --configuration Release
~~~

### Executar em desenvolvimento

~~~powershell
dotnet run --project .\\KitLugia.GUI\\KitLugia.GUI.csproj --configuration Release
~~~

Algumas operações só ficam disponíveis ou produzem resultados completos quando o processo é executado como administrador.

### Compilar a biblioteca Rust

Para gerar a DLL nativa antes do build .NET:

~~~powershell
cargo build --manifest-path .\\rust_native\\Cargo.toml --release
~~~

Depois, compile a solução novamente para que a DLL seja copiada para a saída quando o arquivo existir em rust_native\\target\\release\\.

### Publicar uma versão self-contained

Para uma publicação independente para Windows x64:

~~~powershell
dotnet publish .\\KitLugia.GUI\\KitLugia.GUI.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true
~~~

O script [Deploy.ps1](Deploy.ps1) automatiza o fluxo de build, empacotamento e geração do hash SHA-256 descrito em [DEPLOY.md](DEPLOY.md).

## Estrutura do repositório

~~~text
KitLugia-AVTest/
├── KitLugia.Core/        # Serviços, regras de negócio e operações do Windows
├── KitLugia.GUI/         # Aplicação WPF, páginas, controles e temas
├── KitLugia.Updater/     # Aplicação de atualização
├── rust_native/          # Biblioteca nativa Rust compilada como DLL
├── WinPE_ISO/            # Recursos e ferramentas relacionadas a WinPE/ISO
├── docs/                 # Documentação complementar
├── scripts/              # Scripts e artefatos auxiliares
├── Deploy.ps1            # Automação de build e empacotamento
├── deploy.bat            # Atalho de deploy para Windows
├── DEPLOY.md             # Fluxo de publicação e upload de releases
├── KitLugia.sln          # Solução principal .NET
└── global.json           # Versão do SDK .NET utilizada
~~~

## Organização do código

- **KitLugia.Core:** concentra as operações de sistema, diagnósticos, rede, limpeza, boot e reparos;
- **KitLugia.GUI:** apresenta as funções em páginas e controles WPF reutilizáveis;
- **KitLugia.Updater:** mantém o fluxo de atualização separado da aplicação principal;
- **rust_native:** fornece rotinas nativas compiladas como rust_native.dll;
- **Deploy.ps1 / deploy.bat:** apoiam o empacotamento e a preparação de releases.

## Contribuindo

Contribuições, correções e sugestões são bem-vindas. Antes de abrir um pull request:

1. leia a documentação existente em [docs](docs/), quando aplicável;
2. descreva claramente o problema ou a melhoria;
3. teste as alterações em uma instalação de desenvolvimento do Windows;
4. informe se a mudança exige privilégios de administrador, reinicialização ou ferramentas externas;
5. evite incluir binários gerados, pastas bin, obj ou artefatos locais no commit;
6. mantenha o escopo da alteração pequeno e explique impactos no sistema.

Para mudanças que mexem com Registro, boot, segurança ou rede, inclua no pull request o comportamento esperado e uma forma segura de reverter a alteração.

## Documentação relacionada

- [DEPLOY.md](DEPLOY.md) — build, empacotamento e publicação de releases;
- [AGENTS.md](AGENTS.md) — orientações e contexto para agentes de desenvolvimento;
- [ROADMAP_REVO.md](ROADMAP_REVO.md) — direção e próximos passos do projeto;
- [EXM_TWEAKS_REFERENCE.md](EXM_TWEAKS_REFERENCE.md) — referência de ajustes;
- [LICENSE](LICENSE) — licença MIT.

## Licença

Distribuído sob a licença MIT. Consulte [LICENSE](LICENSE) para o texto completo.

## Autor

Desenvolvido por [Luigi Arruda](https://github.com/luigiarrud4).

Se este projeto for útil para você, considere deixar uma estrela no repositório e compartilhar feedback por meio das issues.
