# Sistema de Categorização de Comandos do KitLugia

## 📋 Visão Geral

O sistema de comandos do KitLugia lista automaticamente **1312 métodos** do assembly `KitLugia.Core` usando reflexão. Este sistema permite:

1. **Listar todos os comandos** disponíveis no KitLugia
2. **Exportar a lista completa** para análise externa
3. **Filtrar por categoria** para encontrar comandos específicos
4. **Pesquisar por texto** em nome, classe, assinatura ou descrição
5. **Executar comandos** diretamente da interface

## 🎯 Como Usar

### 1. Listar Comandos

Ao abrir a página "📋 Catálogo de Comandos", todos os 1312 métodos são automaticamente listados com:

- **Nome do método** (em negrito)
- **Classe** onde está definido
- **Assinatura** completa (parâmetros)
- **Visibilidade** (PUBLIC, PRIVATE, PROTECTED, INTERNAL)
- **Tipo** (STATIC ou INSTANCE) - em amarelo
- **Descrição** (se disponível)

### 2. Exportar Lista

Clique no botão **"📥 Exportar Lista"** no topo da página para salvar a lista completa em formato JSON.

O arquivo exportado contém:
- Data de exportação
- Total de comandos
- Informações detalhadas de cada comando (classe, método, visibilidade, tipo, parâmetros, retorno)

**Uso:** Analise o arquivo exportado para identificar quais comandos são úteis e precisam de descrições.

### 3. Filtrar por Categoria

Use o dropdown **"📂"** para filtrar comandos por categoria:

- **Limpeza** - Comandos de limpeza de sistema
- **Diagnóstico** - Comandos de verificação de sistema
- **Rede** - Configuração e reparo de rede
- **Reparo** - Reparo de arquivos corrompidos
- **Memória** - Otimização de RAM
- **Bloatware** - Remoção de apps pré-instalados
- **Ativação** - Ativação Windows/Office
- **Boot** - Otimização de boot
- **Drivers** - Gerenciamento de drivers
- **GPEDIT** - Configuração gpedit.msc
- **WinBoot** - Instalação Windows
- **ISO** - Manipulação de ISOs
- **Partições** - Gerenciamento de partições
- **Segurança** - Configuração de segurança
- **Privacidade** - Configuração de privacidade
- **Jogos** - Otimização de jogos
- **Serviços** - Gerenciamento de serviços
- **Registro** - Edição do registro
- **Sistema** - Comandos gerais
- **Utilitários** - Ferramentas diversas

### 4. Pesquisar

Use a barra de pesquisa **"🔍"** para filtrar por:
- Nome do método
- Nome da classe
- Assinatura do método
- Descrição

A pesquisa funciona em conjunto com o filtro de categoria.

### 5. Executar Comandos

Clique no botão **"▶ EXECUTAR"** ao lado de cada comando:

- **Métodos estáticos sem parâmetros:** Executa automaticamente e mostra resultado
- **Métodos de instância sem parâmetros:** Tenta criar instância e executar
- **Métodos com parâmetros:** Mostra aviso com lista de parâmetros necessários

## 📝 Como Adicionar Descrições

Edite o arquivo `KitLugia.GUI/Resources/CommandDescriptions.json`:

```json
{
  "descriptions": {
    "CleanupManager.FixDiskFullUsage": "Corrige HD em 100% de uso",
    "NetworkManager.ResetNetworkStack": "Reseta a pilha de rede",
    "NomeDaClasse.NomeDoMetodo": "Descrição do comando"
  }
}
```

**Formato:** `"ClassName.MethodName": "Descrição"`

## 📂 Como Adicionar Categorias

Edite o arquivo `KitLugia.GUI/Resources/CommandCategories.json`:

```json
{
  "categories": {
    "NomeDaCategoria": {
      "description": "Descrição da categoria",
      "classes": ["Class1", "Class2"],
      "methods": ["Method1", "Method2"]
    }
  }
}
```

**Regras:**
- `classes`: Lista de classes que pertencem a esta categoria
- `methods`: Lista de métodos específicos que pertencem a esta categoria
- Um comando pertence à categoria se sua classe OU método estiver na lista

## 🔄 Fluxo de Trabalho Sugerido

1. **Exportar a lista** usando o botão "📥 Exportar Lista"
2. **Analisar o arquivo JSON** para identificar comandos importantes
3. **Adicionar descrições** no `CommandDescriptions.json` para comandos úteis
4. **Adicionar categorias** no `CommandCategories.json` para organizar comandos
5. **Testar filtros** na interface para verificar organização
6. **Executar comandos** para testar funcionalidade

## 📊 Estatísticas Atuais

- **Total de comandos:** 1312
- **Categorias definidas:** 20
- **Comandos com descrição:** 27 (inicial)
- **Comandos categorizados:** ~100 (inicial)

## 💡 Dicas

- Comandos **STATIC** são mais fáceis de executar (não precisam de instância)
- Comandos **sem parâmetros** podem ser executados diretamente da interface
- Use a exportação para análise offline e planejamento
- Adicione descrições apenas para comandos que o usuário realmente precisa usar
- Categorias podem ser expandidas conforme necessário

## 🚀 Próximos Passos

1. Adicionar mais descrições para comandos importantes
2. Expandir categorias para cobrir mais comandos
3. Adicionar sistema de "favoritos" para comandos mais usados
4. Adicionar histórico de execução de comandos
5. Adicionar atalhos para comandos frequentes
