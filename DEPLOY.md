# Deploy — KitLugia-AVTest

## Pré-requisitos
- PowerShell
- .NET SDK
- Git
- GitHub CLI (`gh`) autenticado

## Passo a passo

### 1. Build + ZIP + SHA256
```powershell
.\Deploy.ps1
```
Gera `Publish\KITLUGIA2.zip` e `Publish\KITLUGIA2.zip.sha256`.

### 2. Upload para release existente
```powershell
gh release upload v2.0.20 ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256 --clobber
```

### 3. Commit e push do código
```powershell
git add -A
git commit -m "descrição das mudanças"
git push
```

---

## Caso o gh não tenha token (expirado)
1. Fazer upload manual no GitHub:
   - Abrir https://github.com/luigiarrud4/KitLugia-AVTest/releases
   - Editar release `v2.0.20`
   - Substituir os assets `KITLUGIA2.zip` e `KITLUGIA2.zip.sha256`
2. Commit + push do código via git normalmente.

## Se os tokens do Claude expirarem
1. Rodar `.\Deploy.ps1` manualmente
2. Fazer upload do ZIP pelos passos acima
3. Executar `git add`, `git commit`, `git push` manualmente
