# Force Stop Unlock Tool

Ferramenta para liberar arquivos bloqueados por processos no Windows através do menu de contexto.

## Funcionalidades

- Libera arquivos bloqueados por processos ativos
- Funciona com qualquer tipo de arquivo, incluindo drivers `.sys`
- Integração com menu de contexto do Windows Explorer
- Usa Handle tool do Microsoft Sysinternals
- Funciona em arquivos, pastas e drives

## Arquivos Incluídos

- `Unlock-File.ps1` - Script principal que libera arquivos bloqueados
- `Install-HandleTool.ps1` - Script para instalar o Handle tool
- `AddContextMenu.reg` - Arquivo de registro para adicionar ao menu de contexto
- `RemoveContextMenu.reg` - Arquivo de registro para remover do menu de contexto
- `README.md` - Este arquivo

## Requisitos

- Windows 10 ou superior
- PowerShell 5.1 ou superior
- Permissões de Administrador
- Conexão com internet (para download do Handle tool)

## Instalação

### Passo 1: Instalar o Handle Tool

1. Clique com botão direito em `Install-HandleTool.ps1`
2. Selecione **"Executar como administrador"**
3. Aguarde o download e instalação do Handle tool

O script irá:
- Baixar o Handle tool do site oficial da Microsoft
- Extrair o arquivo `handle64.exe` para esta pasta
- Alternativamente, tentará instalar via winget se o download falhar

### Passo 2: Adicionar ao Menu de Contexto

1. Clique duas vezes em `AddContextMenu.reg`
2. Confirme as mensagens de segurança clicando em **"Sim"** ou **"Yes"**
3. A opção "Force Stop Unlock" será adicionada ao menu de contexto

## Como Usar

### Via Menu de Contexto

1. Clique com botão direito no arquivo/pasta bloqueado
2. Selecione **"Force Stop Unlock"**
3. O script irá:
   - Identificar o processo que está bloqueando o arquivo
   - Tentar liberar o handle do arquivo
   - Se não funcionar, encerrará o processo bloqueador
   - Exibirá o resultado da operação

4. Após a liberação, tente novamente a operação (deletar/mover/renomear)

### Manualmente via PowerShell

```powershell
.\Unlock-File.ps1 -FilePath "C:\caminho\do\arquivo\bloqueado.sys"
```

## Remoção

Para remover a opção do menu de contexto:

1. Clique duas vezes em `RemoveContextMenu.reg`
2. Confirme as mensagens de segurança

## Notas Importantes

⚠️ **AVISO**: Use esta ferramenta com cuidado. Liberar handles ou encerrar processos pode causar instabilidade no sistema se usado incorretamente.

- Não use em arquivos críticos do sistema
- Encerrar processos do sistema pode causar problemas
- Sempre faça backup antes de usar em arquivos importantes
- A ferramenta requer privilégios de administrador

## Solução de Problemas

### Erro: "handle64.exe não encontrado"
- Execute `Install-HandleTool.ps1` como administrador
- Verifique se `handle64.exe` está na pasta `C:\ForceStopUnlock\`

### Erro: "Este script precisa ser executado como Administrador"
- Execute o script como administrador
- O menu de contexto também requer elevação automática

### A opção não aparece no menu de contexto
- Verifique se você executou `AddContextMenu.reg`
- Reinicie o Windows Explorer após adicionar o registro
- Verifique se o caminho em `AddContextMenu.reg` está correto (`C:\ForceStopUnlock\`)

## Como Funciona

1. O Handle tool lista todos os handles abertos no sistema
2. O script filtra para encontrar handles do arquivo alvo
3. Para cada handle encontrado:
   - Tenta liberar o handle sem encerrar o processo
   - Se falhar, encerra o processo bloqueador
4. O arquivo fica livre para operações de arquivo

## Licença

Esta ferramenta usa o Handle tool do Microsoft Sysinternals, que é propriedade da Microsoft.

## Suporte

Para problemas ou dúvidas, verifique a documentação oficial do Handle tool:
https://learn.microsoft.com/en-us/sysinternals/downloads/handle
