# ADR-010: Documentação visual obrigatória para todo módulo publicado

Status: **Aceito**  
Data: 2026-11-02  
Autores: Lugia + Hermes Agent

## Contexto
O projeto KitLugia cresceu para ~12 arquivos >500 linhas e múltiplos módulos críticos (DeepUninstaller, RegistryOwnership, HunterWindow). Documentação textual em `docs/` existe, mas a compreensão arquitetural depende de leitura de código cru — custo alto para onboarding e manutenção.

## Decisão
Adotar documentação visual gerada automaticamente como artefato obrigatório de cada módulo:
- Diagramas de classes (Archify) para cada agrupamento de tipos.
- Diagramas de workflow (Archify) para fluxos públicos críticos (Hunter, AutoCleanup, RegistryOwnership).
- AST Graphify para todo diretório (Core, GUI, docs) para navegação em tempo de build.
- Compressão Caveman para arquivos >200 linhas — reduz tokens em chat tools.

## Motivo
- Class diagram reduz entendimento de dependências de horas para minutos.
- Workflow diagrams documentam decisões de fluxo que não aparecem em comentários.
- Graphify AST permite busca por função/assinatura sem ler o arquivo inteiro.
- Caveman compression reduz custo de contexto em Hermes sem perder informação.

## Consequências
- Toda PR que adiciona módulo novo deve incluir arquivo `.archify/<nome>.html` e `.graphify_ast.json`.
- Scripts de build devem executar `graphify extract` incrementalmente.
- Backups Hermes/KitLugia existem (`0d0ecff` + `.zip`).

## Revisão
Revisar quando ADR-001 (fail-closed) mudar para permitir auto-cleanup de score <100.
