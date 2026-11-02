# IntegrityPage — Especificação de comportamento (consolidado)

## 1. Classificação dos itens

Cada item (Tweak) tem dois atributos independentes:

- **Status**: `OK` / `MODIFIED` / `PENDING` / `NOT_FOUND`
- **IsOptional**: bool (itens opcionais, ex: VBS, etc.)

Combinados, definem a categoria de exibição:

| Status | IsOptional | Categoria (filtro) | Badge exibida | Botão por item |
|--------|------------|--------------------|---------------|----------------|
| OK | false | Protegido | ✅ OK | Desativar (verde) |
| MODIFIED | false | Modificado | ⚠️ MODIFICADO | CORRIGIR (vermelho) |
| PENDING | false | (nenhum — aparece no banner) | ⏳ AGUARDANDO REINÍCIO | (oculto) + selo 🔄 no título |
| NOT_FOUND | false | (nenhum) | N/A | (oculto) |
| OK | true | Opcional | ◇ OPCIONAL | CORRIGIR (amarelo) |
| MODIFIED | true | Opcional | ◇ OPCIONAL (FIXA) | REVERTER (borda dourada) |
| PENDING | true | Opcional | ⏳ AGUARDANDO REINÍCIO | (oculto) + selo 🔄 no título |
| NOT_FOUND | true | Opcional | N/A | (oculto) |

## 2. Regra da badge de opcionais

**A badge de itens opcionais é FIXA "◇ OPCIONAL" nos estados estáveis (OK e MODIFIED).**
Só o botão muda: CORRIGIR (quando OK) / REVERTER (quando MODIFIED).

A badge NÃO vira "⚠️ MODIFICADO" quando um opcional é modificado. Se a badge
mudasse conforme o Status, a separação dos filtros (Protegido / Modificado / Opcional)
quebraria: um item opcional modificado apareceria com badge de modificado no filtro errado.

Estados transitórios (PENDING, NOT_FOUND) continuam visíveis também nos opcionais
(trigger declarado depois do badge opcional).

## 3. Filtros

Mutuamente exclusivos por `IsOptional`, **não** por Status:

- **"Protegido"** = `Status == OK && !IsOptional`
- **"Modificado"** = `Status == MODIFIED && !IsOptional` (só críticos)
- **"Opcional"** = `IsOptional` (TODOS os opcionais, com ou sem otimização aplicada)

Um item opcional modificado fica em "Opcional" com botão REVERTER. Nunca some nem aparece
em "Modificado". "Opcional" e "Modificado" são filtros mutuamente exclusivos.

## 4. Botão "RESTAURAR TODOS" (vermelho, grande)

- Corrige **APENAS** itens `MODIFIED && !IsOptional` (vulnerabilidades reais).
- Itens OPCIONAIS são mantidos como estão (são escolha do usuário).
- Visível somente quando score < 100.
- Confirm dialog explicita que opcionais não são tocados.
- Tooltip: "Corrige apenas os itens MODIFICADOS (vulnerabilidades reais). Itens OPCIONAIS são mantidos como estão — eles são escolha sua e não contam para o score."

## 5. Score de integridade

- Conta apenas não-opcionais (`IsOptional == false`).
- `PENDING` excluído do denominador e de `badCountable`.
- `badCountable` = MODIFIED (não-opcionais).
- score = `100 - ceil(100 * badCountable / countableTweaks)`.
- Se todos não-opcionais OK → score 100 → BtnFixAll oculto.

## 6. Arquivos envolvidos

- `KitLugia.GUI/Pages/IntegrityPage.xaml.cs` — filtros, contagens, BtnFixAll_Click, RecalculateScore.
- `KitLugia.GUI/Pages/IntegrityPage.xaml` — DataTemplate triggers (badge opcional fixa declarada antes de PENDING/NOT_FOUND), BtnFixAll com ToolTip.
- `KitLugia.GUI/Converters/OptionalNotModifiedConverter.cs` — tooltip por item reflete badge fixa.

## 7. Pitfalls

- NÃO mudar a badge de opcionais conforme Status — quebra a separação de filtros.
- NÃO incluir opcionais no filtro "Modificado" — eles ficam em "Opcional".
- NÃO tocar opcionais no "RESTAURAR TODOS" — só críticos.
- NÃO alterar a ordem dos DataTriggers: badge opcional deve vir ANTES de PENDING/NOT_FOUND para que estados transitórios prevaleçam.


## 8. Pós-escrita (ToggleTweak) — verificação real do valor

Após escrever, `Guardian.ToggleTweak` re-executa `CheckTweak(tweak)` para confirmar
que o valor mudou. A falha ("foi gravado mas continua com o valor errado") só é
reportada quando o Windows revertou a escrita:

- Escreveu valor SEGURO (`applySafeValue=true`) → esperado `OK`. Falha se ficar `MODIFIED`.
- Escreveu valor OTIMIZADO (`applySafeValue=false`) → esperado `MODIFIED`. Falha se ficar `OK`.

Isso é essencial para itens opcionais (ex.: Spooler): aplicar a otimização deixa
o status em `MODIFIED` de propósito (badge fixa "◇ OPCIONAL" + botão REVERTER) e
NÃO é falha. Antes, qualquer `MODIFIED` pós-escrita era tratado como falha, gerando
o falso "foi gravado mas continua com o valor errado" no Spooler.

## 9. Atualização de contagens após alterações

`RefreshFromAllTweaks()` (chamado após cada toggle e após "Restaurar Todos")
reconstrói as contagens do ComboBox via `BuildFilterOptions()` antes de
`RecalculateScore()` / `ApplyFiltersAndUpdateList()`. Assim os números dos filtros
(Protegido/Modificado/Opcional/Segurança) atualizam na hora, sem precisar sair
e voltar na página. A seleção atual é preservada e `_rebuildingFilters` evita
reentrância no `CategoryFilter_SelectionChanged`.
