# Deploy — KitLugia-AVTest

## Pré-requisitos
- PowerShell
- .NET SDK
- Git
- GitHub CLI (`gh`) autenticado em `github.com`

O passo 2 do `deploy.bat` **ativa e valida a conta `luigiarrud4`**:

1. `gh auth status` (login se necessário);
2. `gh api user --jq .login` — se não for `luigiarrud4`, tenta
   `gh auth switch -u luigiarrud4` e, em último caso, `gh auth login -h github.com -w`;
3. `gh auth setup-git` — faz o `git` usar o **mesmo token** do `gh`;
4. `git credential fill` — confirma que o `username` do git também é `luigiarrud4`.

Qualquer divergência aborta o deploy **antes do build**, com mensagem clara. Sem
isso, uma credencial antiga no Gerenciador de Credenciais do Windows faz o
`git push` falhar com `403` enquanto o `gh release` funciona — um "meio deploy"
silencioso.

## Fluxo rápido (recomendado)

```batch
deploy.bat
```

O script faz tudo automaticamente:

```
===============================================
 Version Detection: SUCCESS
===============================================

 Source:        gh-cli
 Current:       v2.0.65
 Next:          v2.0.66

===============================================

 Press ENTER to publish v2.0.66
 Or type a different version, ex: 2.1.0

 Version:  <pressione ENTER>
```

### Passos executados

| Passo | Descrição |
|-------|-----------|
| 1/6 | Detecta a próxima versão (5 fallbacks + piso de tags locais) |
| 2/6 | Ativa/verifica a conta `luigiarrud4` no `gh` **e** no `git` (`gh auth setup-git` + `git credential fill`) |
| 3/6 | Build + ZIP + SHA256 via `Deploy.ps1`, e valida que os dois arquivos existem |
| 4/6 | **`git add -A` + commit + `git push origin HEAD:main`** |
| 5/6 | Cria a release `--target main` com **`--generate-notes`** (ou só substitui os assets se já existe) |
| 6/6 | Verifica a tag remota e lista os assets publicados |

### Regras que não devem mudar

1. **Código primeiro, release depois.** `gh release create` cria a tag a partir
   do HEAD da branch padrão no GitHub. Se a release for criada antes do push, a
   tag aponta para o commit antigo enquanto o ZIP é do código novo.
2. **`HEAD:main`, nunca `main` sozinho.** `git push origin main` empurra o *ref
   local* `main`, não o branch atual. Trabalhando no `aaa`, isso publicava o
   `main` desatualizado e deixava todo o trabalho novo apenas local. `HEAD:main`
   publica o commit atual em `origin/main` (fast-forward).
3. **A tag pertence ao `gh`.** Removemos `git tag -f` + `git push --force` +
   `git push --delete` do fluxo normal. Se existir uma tag órfã (tag sem
   release), o passo 5 a reaponta para o HEAD antes de criar a release.
4. **Toda falha aborta.** `commit`, `push`, `release create` e `release upload`
   têm checagem de `errorlevel`, com mensagens distintas para `commit` e `push`.
   O banner "PUBLISHED!" só aparece se tudo deu certo.
5. **Erro de commit ≠ erro de auth.** Um `git commit` rejeitado costuma ser o
   hook `.githooks/pre-commit` (`ui_style_audit`), não credencial. O passo 2 já
   provou que a conta está certa; a mensagem do passo 4 aponta o hook e o remédio.

### Arquivos enviados

- **Release (binários):** `Publish/KITLUGIA2.zip` + `KITLUGIA2.zip.sha256`.
  Esses nomes são fixos — `KitLugia.Core/GitHubUpdater.cs` e
  `KitLugia.GUI/Pages/UpdatePage.xaml.cs` procuram `KITLUGIA2.zip` por nome.
- **Repositório (código):** `git add -A` pega apenas arquivos-fonte/docs não
  ignorados. `Publish/` está coberto por `.gitignore` (`publish/`, com
  `core.ignorecase=true` no Windows), então o ZIP de ~74 MB, o `.exe` e as DLLs
  **não** são commitados. Atenção: em um checkout case-sensitive (Linux/macOS),
  `Publish/` não casaria com `publish/` — adicione `/Publish/` ao `.gitignore`
  se for buildar fora do Windows.

---

## Descrição da release (notas automáticas do GitHub)

O passo 5 usa **`--generate-notes`** — o equivalente pelo CLI do botão
**"Generate release notes"** da interface do GitHub (Release Notes API, o
recurso automático/IA que a interface mostra ao criar uma release). Ele monta a
descrição com os PRs mergeados, os contribuidores e o link para o changelog
completo desde a release anterior. O título continua sendo `KitLugia vX.Y.Z`
(`--title`), porque `--generate-notes` também geraria um título automático.

Antes dessa mudança a descrição era o texto fixo
`--notes "Release automatica vX.Y.Z"` (era literalmente o corpo da `v2.0.64`).
Nenhuma anotação interna da IA entra aqui: `.workbuddy-ai/` está no
`.gitignore` e não é enviada ao GitHub.

Se o `--generate-notes` falhar (rate limit, indisponibilidade da API), o
passo 5 tenta uma segunda vez com `--notes` mínimo: o código já foi
empurrado e a release não pode morrer por causa da API de notas. O passo 6
imprime os assets e o corpo das notas geradas para conferência.

> **Limitação real deste repositório:** hoje não há PRs mergeados (`gh pr list
> --state merged` → `[]`), e o gerador do GitHub lista **PRs, não commits
> diretos**. Então a descrição gerada sai apenas como
> `**Full Changelog**: https://github.com/.../compare/v2.0.62...v2.0.66`
> (igual aos rascunhos "Teste de Notas IA" já existentes). Depois que o passo 4
> empurrar o HEAD para `main`, a tag `v2.0.64` passa a ser ancestral e o
> intervalo do changelog fica mais correto.
>
> Para enriquecer sem abrir mão das notas automáticas, duas opções:
> - `--generate-notes --notes "Resumo da versão..."` — o `gh` **prepend** o texto
>   customizado às notas geradas;
> - criar `.github/release.yml` para categorizar por labels (só ajuda se houver
>   PRs com labels).

Nota: o ramo "release já existe" faz só `gh release upload --clobber` e **não**
regera a descrição — ela fica como estava na criação da release.

---

## Detecção de versão (5 fallbacks + piso de tags)

O `kl_deploy_get_version.ps1` tenta detectar a versão de 5 fontes, na ordem:

| # | Fonte | Ferramenta | Quando funciona |
|---|-------|-----------|-----------------|
| 1 | GitHub CLI | `gh release view` | `gh` instalado + autenticado |
| 2 | PowerShell nativo | `Invoke-RestMethod` | Internet + PowerShell com acesso web |
| 3 | curl | `curl.exe` + regex | PowerShell sem acesso web |
| 4 | Tags locais | `git tag --sort=-v:refname` | Offline |
| 5 | Erro | Arquivo de info | Tudo falhou → pede versão manual |

A próxima versão incrementa o patch: `v2.0.52` → `2.0.53`, `v2.0.99` → `2.0.100`.

**Piso de tags:** depois da detecção, o script lê a maior tag `X.Y.Z` local e
nunca sugere uma versão já marcada. Isso evita colidir com tags órfãs (ex.:
`v2.0.65` existir no remoto sem release correspondente) e elimina a divergência
entre a sugestão online (`2.0.65`) e a offline (`2.0.66`).

Em caso de falha total, o arquivo `kl_deploy_info.txt` é escrito em
`chave=valor` — o mesmo formato que o `deploy.bat` parseia.

---

## Passo a passo manual (sem deploy.bat)

Respeite a ordem: **commit/push antes da release**.

```powershell
# 1. Build + ZIP + SHA256
.\Deploy.ps1 -Version 2.0.66

# 2. Ativa a conta correta e alinha o git (evita 403)
gh api user --jq .login            # deve imprimir luigiarrud4
gh auth switch -u luigiarrud4      # ou: gh auth login -h github.com -w
gh auth setup-git

# 3. Commit e push do código para main
git add -A
git commit -m "Deploy v2.0.66"
git push origin HEAD:main

# 4. Release (cria a tag no main já atualizado, descricao gerada pelo GitHub)
gh release create v2.0.66 --repo luigiarrud4/KitLugia-AVTest --target main `
  --title "KitLugia v2.0.66" --generate-notes `
  ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256

# Se a release já existir, apenas substitua os assets:
# gh release upload v2.0.66 --repo luigiarrud4/KitLugia-AVTest `
#   ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256 --clobber
```

---

## Testes

```batch
test_deploy_version.bat
```

8 blocos de verificação: execução do PowerShell, criação dos arquivos de saída,
formato `X.Y.Z`, estrutura do `kl_deploy_info.txt`, incremento do patch,
validade do campo `source`, leitura pelo `deploy.bat` e parsing de versão em 4
casos. O script usa o fallback de tags locais automaticamente quando não há
acesso ao GitHub.

---

## Solução de problemas

### `remote: Permission to ... denied to <outra conta>` / `403`
O passo 2 agora detecta e corrige isso automaticamente. Para conferir à mão:

```powershell
gh api user --jq .login        # deve ser luigiarrud4
gh auth switch -u luigiarrud4  # se estiver em outra conta
gh auth setup-git
printf 'protocol=https\nhost=github.com\n\n' | git credential fill  # username=luigiarrud4
git push --dry-run origin HEAD:main
```

Se ainda falhar, remova `git:https://github.com` do Gerenciador de Credenciais
do Windows e autentique novamente.

### `ERROR: git commit was REJECTED` / `Commit bloqueado pelo ui_style_audit`
**Não é problema de auth** — o passo 2 já validou a conta. O culpado é o hook
`.githooks/pre-commit` (`core.hooksPath=.githooks`), que roda o
`tests/ui_style_audit.py --check` (F7 do plano de consistência de UI). Ele
reprova o commit quando há títulos/subtítulos fora do tema ou **hex hardcoded
acima da allowlist por arquivo** (`tests/ui_style_allowlist.txt`).

Exemplo real (11/11): `AppsPage.xaml: 141 hex (allowlist 134)` e
`PartitionsPage.xaml: 59 hex (allowlist 44)`.

Duas saídas:

```powershell
# A) Aceitar o novo patamar (mudança intencional) — regrava os tetos atuais
py -3 tests/ui_style_audit.py --baseline

# B) Manter o F7 rígido: trocar as cores novas por StaticResource do tema
#    e só então commitar
```

O `deploy.bat` imprime o relatório e o comando de correção no próprio erro.
Arquivos ficam **staged**, o build já foi feito e nenhuma release é publicada —
é seguro rodar `deploy.bat` de novo depois de resolver.

> Dica: para antecipar o problema sem esperar o build, rode
> `py -3 tests/ui_style_audit.py --check` antes de iniciar o deploy.

### `deploy.bat` sugere uma versão estranha
Verifique `%TEMP%\kl_deploy_ver.txt` e `%TEMP%\kl_deploy_info.txt`. Se as tags
locais estiverem à frente das releases, o piso de tags prevalece — digite a
versão desejada manualmente no prompt.

### Tag existe, mas não há release
O passo 5 detecta a tag órfã, faz `git tag -f <tag> HEAD` + push forçado **da
tag** e só então cria a release. Assim o código-fonte da release corresponde ao
ZIP enviado.

### Token do `gh` expirado
1. `gh auth login -h github.com -w`
2. `gh auth setup-git`
3. Rodar `deploy.bat` novamente. O passo 4 aborta antes de publicar qualquer
   release se o push falhar, então é seguro repetir.

### Upload manual (fallback extremo)
Abrir https://github.com/luigiarrud4/KitLugia-AVTest/releases, editar a release
e substituir os assets `KITLUGIA2.zip` e `KITLUGIA2.zip.sha256`. Depois fazer o
commit + push do código normalmente.
