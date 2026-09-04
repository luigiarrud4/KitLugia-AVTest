# 📐 Convenções de Páginas — KitLugia GUI

## Checklist ao Criar uma Nova Página

- [ ] Copiar `_PageTemplate.xaml` + `_PageTemplate.xaml.cs` e renomear
- [ ] Namespace: `KitLugia.GUI.Pages` (ou sub-namespace para WindowsSettings)
- [ ] Registrar `PageType` no `MainWindow.xaml.cs` (switch `NavigateToPage`)
- [ ] Adicionar botão de navegação no `DashboardPage.xaml` (se aplicável)
- [ ] Salvar arquivos como **UTF-8 com BOM**
- [ ] Testar navegação: Dashboard → sua página → voltar (verificar RAM no log)

## Encoding (UTF-8)

**Regra:** NUNCA usar caracteres latinos diretamente no XAML ou C# sem proteção.

| Método | Onde usar | Exemplo |
|--------|-----------|---------|
| Entidades XML | XAML | `&#xE7;` = ç, `&#xF5;` = õ, `&#xC9;` = É |
| Unicode escape | C# strings | `\u00E7` = ç, `\u00F5` = õ |
| Literais | C# (se encoding garantido) | `ç`, `õ` (só com UTF-8 BOM) |

**Caracteres problemáticos comuns:**
- `ç` → `&#xE7;` (XAML) ou `\u00E7` (C#)
- `ã` → `&#xE3;` (XAML) ou `\u00E3` (C#)
- `õ` → `&#xF5;` (XAML) ou `\u00F5` (C#)
- `é` → `&#xE9;` (XAML) ou `\u00E9` (C#)
- `ê` → `&#xEA;` (XAML) ou `\u00EA` (C#)
- `á` → `&#xE1;` (XAML) ou `\u00E1` (C#)
- `ó` → `&#xF3;` (XAML) ou `\u00F3` (C#)
- `ú` → `&#xFA;` (XAML) ou `\u00FA` (C#)
- `É` → `&#xC9;` (XAML) ou `\u00C9` (C#)
- `⚡` → `&#x26A1;` (XAML) ou `\u26A1` (C#)
- `🛡️` → `&#x1F6E1;&#xFE0F;` (XAML)

## Cleanup (OBRIGATÓRIO)

Todo `Page` deve ter:

```csharp
// 1. No construtor — SEMPRE
this.Unloaded += SuaPagina_Unloaded;

// 2. Handler
private void SuaPagina_Unloaded(object sender, RoutedEventArgs e)
{
    Cleanup();
}

// 3. Método público (chamado via reflection pelo MainWindow)
public void Cleanup()
{
    // a) Cancelar CTS
    _cts?.Cancel();
    _cts?.Dispose();
    _cts = null;

    // b) Parar timers
    _timer?.Stop();
    _timer = null;

    // c) Unsubscribir eventos
    this.Loaded -= SuaPagina_Loaded;
    this.Unloaded -= SuaPagina_Unloaded;

    // d) Limpar dados
    MinhaCollection?.Clear();

    // e) Liberar DataContext
    this.DataContext = null;
}
```

**Por quê?** O `MainWindow.CleanupAndNavigate` usa reflection para chamar `Cleanup()` em cada página ao navegar. Sem ele, a página anterior fica retida na memória (memory leak).

## Timers

| Tipo | Como parar no Cleanup | Notas |
|------|----------------------|-------|
| `DispatcherTimer` | `_timer.Stop(); _timer = null;` | Roda na UI thread, seguro |
| `System.Timers.Timer` | `_timer.Stop(); _timer.Dispose(); _timer = null;` | Roda em background, precisa Dispose |
| `PeriodicTimer` | `_cts.Cancel();` (via token) | Usa CancellationToken |

## CancellationTokenSource

```csharp
// Padrão seguro:
_cts?.Cancel();    // cancela trabalho anterior
_cts?.Dispose();   // libera handle nativo
_cts = null;       // evita referência stale

// Na criação:
_cts = new CancellationTokenSource();
var token = _cts.Token;

// No Task.Run:
await Task.Run(() => { ... }, token);

// Se o token for de longa duração (não-descartável):
// Não chame Dispose — apenas Cancel + null
```

## Event Handlers

```csharp
// SEMPRE unsubscriver no Cleanup:
this.Loaded -= handler;
this.Unloaded -= handler;
someButton.Click -= handler;
someTimer.Tick -= handler;

// Exceção: handlers anônimos (lambda) não podem ser unsubscriver
// → usar métodos nomeados em vez de lambdas quando possível
```

## DataContext

```csharp
// SEMPRE no Cleanup:
this.DataContext = null;

// Por quê?
// - Libera todos os bindings WPF
// - Permite GC coletar a página e seus ViewModels
// - Reduz RAM visível no Task Manager
```

## Padrões Visuais (XAML)

### Estrutura Base
```xml
<ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="40,30,40,50" MaxWidth="900" HorizontalAlignment="Left">
        <TextBlock Text="Título" FontSize="32" FontWeight="SemiBold" Foreground="White"/>
        <TextBlock Text="Subtítulo" Foreground="#A0A0A0" Margin="0,0,0,30"/>
        
        <!-- Cards aqui -->
    </StackPanel>
</ScrollViewer>
```

### Card com Toggle
```xml
<Border Background="{StaticResource CardBackground}" 
        BorderBrush="{StaticResource CardBorder}" 
        BorderThickness="1" CornerRadius="8">
    <StackPanel>
        <Grid Margin="20">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>  <!-- Info button -->
                <ColumnDefinition Width="*"/>     <!-- Texto -->
                <ColumnDefinition Width="Auto"/>  <!-- Status -->
                <ColumnDefinition Width="Auto"/>  <!-- Toggle -->
            </Grid.ColumnDefinitions>
            
            <controls:InfoButton Grid.Column="0" ToolTipText="..."/>
            <StackPanel Grid.Column="1">
                <TextBlock Text="Nome" FontSize="15" Foreground="White"/>
                <TextBlock Text="Descrição" FontSize="12" Foreground="#808080"/>
            </StackPanel>
            <TextBlock x:Name="Status" Grid.Column="2" Foreground="Gray" FontWeight="SemiBold"/>
            <CheckBox x:Name="Chk" Grid.Column="3" Style="{StaticResource ToggleSwitchStyle}"/>
        </Grid>
        <Separator Background="{StaticResource CardBorder}"/>
        <!-- Próximo item -->
    </StackPanel>
</Border>
```

### Cores de Status
| Estado | Cor | Uso |
|--------|-----|-----|
| Ativo/aplicado | `#6CB55F` (verde) | Status label |
| Padrão/inativo | `#888888` (cinza) | Status label |
| Destaque/slide | `#FFAA00` (amarelo) | Slide labels |
| Alerta | `#FF6F61` (vermelho) | Erros |
| Info | `#888888` | Subtítulos |

### Info Button
```xml
<controls:InfoButton Grid.Column="0" 
    ToolTipText="Descrição detalhada para o tooltip."/>
```

## Padrões da Auditoria (03/09/2026)

Padrões observados em 49 páginas que devem entrar em qualquer página nova:

### 1. Guarda anti-reentrância em tick async (OBRIGATÓRIO)

Se o `Tick` de um timer dispara trabalho async (WMI, queries, scans) que pode
levar MAIS tempo que o intervalo do timer, ticks se sobrepõem e empilham
chamadas. Exemplo real corrigido: `NetworkPage.RefreshAdapterStatus` (tick 3s,
`ListPhysicalAdapters` WMI podia passar de 3s).

```csharp
private bool _refreshing; // campo

// Tick:
if (_refreshing) return;  // descarta tick que chegou durante o anterior
_refreshing = true;
try { _ = RefreshAsync(); }
finally { _refreshing = false; }
```

Regra: **todo tick async precisa de guard**, mesmo com intervalo grande.

### 2. Brushes cacheados, nunca por tick

`new SolidColorBrush` por tick/por status = alocação + re-render a cada
atualização. Criar `readonly` fields e reutilizar:

```csharp
private readonly SolidColorBrush _brushActive = new(Color.FromRgb(108, 203, 95));
private readonly SolidColorBrush _brushDefault = new(Color.FromRgb(150, 150, 150));
```

Páginas que seguem: NetworkPage, TweaksPage. Exemplo de NÃO seguir: criar
brush dentro de método chamado por timer.

### 3. Eventos estáticos: unsubscribe OBRIGATÓRIO no Cleanup

`WinbootManager.OnLogUpdate`, `InstallMonitor.OnChange`, etc. são estáticos —
a página fica presa na memória para sempre se não desinscrever:

```csharp
public void Cleanup()
{
    WinbootManager.OnLogUpdate -= MeuHandler;
    InstallMonitor.OnChange -= MeuHandler;
}
```

### 4. Unloaded = rede de segurança do Cleanup

O `Cleanup()` via reflection do `MainWindow.CleanupAndNavigate` **não roda**
quando a navegação usa `MainFrame.Navigate()` direto. O hook `Unloaded`
SEMPRE dispara. Então: registrar `Unloaded` no construtor, chamar `Cleanup()`
no handler, e NUNCA depender só da chamada via reflection.

### 5. Listas com refresh por segundo: update diff, não Clear+Add

Para grids que atualizam a cada 1s (ProcessMonitorPage): manter a coleção e
fazer update dos itens existentes / add novos / remover sumidos. `Clear()` +
re-Add de centenas de itens a cada tick gera churn de bindings + scroll salta.

### 6. Timer com Tick que re-renderiza UI: parar no Unloaded SEMPRE

`_timer.Stop()` + `_timer = null` no Cleanup. Verificado em: PartitionsPage,
NetworkPage, PrivacyPage, DiagnosticPage, ProcessMonitorPage, TraySettingsPage,
StoreRemakePage, WinpeToolsPage — todos seguem, nenhum vaza.

## Anti-Patterns (NÃO FAZER)

1. **❌ Usar `Thread.Sleep`** — trava a UI
2. **❌ Usar `GC.Collect()` sem gate** — causa micro-freezes
3. **❌ Deixar CTS sem Dispose** — leak de handle nativo
4. **❌ Usar lambda no `Unloaded += (s,e) => {...}`** — não pode ser unsubscrito
5. **❌ Esquecer `DataContext = null`** — retém toda a árvore de bindings
6. **❌ Caracteres latinos sem escape** — quebra em builds com encoding diferente
7. **❌ Usar `using static` em `GC.MaxGeneration`** — precisa ser qualificado
8. **❌ Fazer trabalho pesado no construtor** — usar `Loaded` + `async`
