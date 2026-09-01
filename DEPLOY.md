# Deploy — KitLugia-AVTest

## Pré-requisitos
- PowerShell
- .NET SDK
- Git
- GitHub CLI (`gh`) autenticado

## Fluxo rápido (recomendado)

```batch
deploy.bat
```

O script faz tudo automaticamente:

```
===============================================
  Detecção de Versão: SUCESSO
===============================================

  Fonte:        gh-cli
  Atual:        v2.0.52
  Próxima:      v2.0.53

===============================================

  ENTER = publicar v2.0.53
  Ou digite outra versão (ex: 2.1.0)

  Version:  <pressione ENTER>
```

### Passos executados

| Passo | Descrição |
|-------|-----------|
| 1/6 | Detecta versão mais recente no GitHub (5 fallbacks) |
| 2/6 | Autentica com `gh auth` |
| 3/6 | Build + ZIP + SHA256 via `Deploy.ps1` |
| 4/6 | Cria release no GitHub (ou sobe assets se já existe) |
| 5/6 | Git commit |
| 6/6 | Git push + tag |

---

## Detecção de versão (5 fallbacks)

O `kl_deploy_get_version.ps1` tenta detectar a versão de 5 fontes, na ordem:

| # | Fonte | Ferramenta | Quando funciona |
|---|-------|-----------|-----------------|
| 1 | GitHub CLI | `gh release view` | `gh` instalado + autenticado |
| 2 | PowerShell nativo | `Invoke-RestMethod` | Internet + PowerShell com acesso web |
| 3 | curl | `curl.exe` + regex | PowerShell sem acesso web |
| 4 | Tags locais | `git tag --sort=-v:refname` | Offline |
| 5 | Erro | Arquivo de info | Tudo falhou → botão pede versão manual |

A próxima versão é calculada automaticamente incrementando o patch:
- `v2.0.52` → sugere `2.0.53`
- `v2.0.99` → sugere `2.0.100`

---

## Passo a passo manual (sem deploy.bat)

### 1. Build + ZIP + SHA256
```powershell
.\Deploy.ps1
```
Gera `Publish\KITLUGIA2.zip` e `Publish\KITLUGIA2.zip.sha256`.

### 2. Upload para release existente
```powershell
gh release upload v2.0.52 ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256 --clobber
```

### 3. Commit e push do código
```powershell
git add -A
git commit -m "descrição das mudanças"
git push
```

---

## Testes

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File test_deploy_version.ps1
```

Suite de 28 testes:
- Parsing de versão (formatação, bordas, rejeição de formato inválido)
- Formato dos arquivos de saída (compatibilidade com deploy.bat)
- Integração ao vivo com a API do GitHub

### Testar sem acesso ao GitHub
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File test_deploy_version.ps1 -SkipIntegration
```

---

## Caso o gh não tenha token (expirado)
1. Fazer upload manual no GitHub:
   - Abrir https://github.com/luigiarrud4/KitLugia-AVTest/releases
   - Editar release
   - Substituir os assets `KITLUGIA2.zip` e `KITLUGIA2.zip.sha256`
2. Commit + push do código via git normalmente.

## Se os tokens do Claude expirarem
1. Rodar `.\Deploy.ps1` manualmente
2. Fazer upload do ZIP pelos passos acima
3. Executar `git add`, `git commit`, `git push` manualmente
