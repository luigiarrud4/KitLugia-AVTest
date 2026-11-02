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
| Archify | ✅ ADOTAR | Diagramas interativos geram valor para onboarding/revisão. CLI funciona no Windows. |
| Graphify | ✅ ADOTAR | AST por diretório viabiliza busca por assinatura sem leitura integral. |
| Ponytail | ❌ DESCARTAR | Findings genéricos; não agrega além do code review já existente. |
| Understand Anything | ❌ DESCARTAR | Sem CLI; biblioteca Node instável; sem ganho prático mensurável. |
| Agent Skills | ⏸️ REEVALUAR | Não testado a fundo; skills do Hermes específicas entregaram mais valor. |

## Métricas de impacto

| Métrica | Valor |
|---------|-------|
| Arquivos em code review | 64 |
| Arquivos em ponytail analysis | 46 |
| Arquivos `.caveman` gerados | 22 |
| Diagramas Archify validados | 15 |
| Diretórios Graphify extraídos | 3 (Core 182 files, GUI, docs) |
| Build do KitLugia.GUI | 0 erros, 154 warnings pré-existentes |
| Harness autoclean | 35/35 OK |
| Harness CertaintyHarness | 16/16 PASS |
| Harness DiagnosticCenterRun | todas verificações OK |

## Decisão

Três skills valem a pena (Caveman, Archify, Graphify) e serão mantidas no fluxo.
Três skills não valem a pena e serão removidas da instalação global.
Nenhuma skill quebrou o build ou os harnesses de runtime.
