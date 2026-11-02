# OpenCode Skills — Veredito final de adoção no KitLugia

Data: 2026-11-02
Autor: Lugia + Hermes Agent

## Skills testadas
1. Caveman (compressão)
2. Archify (diagramas)
3. Graphify (AST)
4. Ponytail (simplicidade)
5. Understand Anything (análise semântica)
6. Agent Skills (TDD/review)

## Veredito

| Skill | Decisão | Justificativa |
|-------|---------|---------------|
| Caveman | ✅ ADOTAR | Compressão preserva conteúdo; reduz tokens em contexto. Retorno imediato. |
| Graphify | ✅ ADOTAR | AST por diretório viabiliza busca por assinatura sem leitura integral. |
| Archify | ❌ DESCARTAR | Diagramas HTML são visualmente interessantes, mas sem utilidade prática no dia a dia. Não ajuda no build, debug ou refatoração. |
| Ponytail | ❌ DESCARTAR | Findings genéricos; não agrega além do code review já existente. |
| Understand Anything | ❌ DESCARTAR | Sem CLI; biblioteca Node instável; sem ganho prático mensurável. |
| Agent Skills | ⏸️ REEVALUAR | Não testado a fundo; skills do Hermes específicas entregaram mais valor. |

## Métricas de impacto

| Métrica | Valor |
|---------|-------|
| Arquivos em code review | 64 |
| Arquivos em ponytail analysis | 46 |
| Arquivos `.caveman` gerados | 22 |
| Diagramas Archify gerados | 15 (removidos do repo) |
| Diretórios Graphify extraídos | 3 (Core, GUI, docs) |
| Build do KitLugia.GUI | 0 erros, 154 warnings pré-existentes |
| Harness autoclean | 35/35 OK |
| Harness CertaintyHarness | 16/16 PASS |
| Harness DiagnosticCenterRun | OK |
| Patch aplicado | RecycleBinOp.cs: comentário de flag corrigido |

## Decisão

Duas skills valem a pena (Caveman, Graphify) e serão mantidas no fluxo.
Quatro skills não valem a pena e foram removidas (Archify, Ponytail, Understand Anything, Agent Skills).
Nenhuma skill quebrou o build ou os harnesses de runtime.
