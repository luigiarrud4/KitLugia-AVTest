# ModalDialog - Exemplo de Uso

## Descrição
UserControl reutilizável para criar diálogos modais com overlay, botão de fechar e scroll automático.

## Propriedades

- **Title** (string): Título do modal (padrão: "TÍTULO")
- **DialogMaxWidth** (double): Largura máxima do modal em pixels (padrão: 600)
- **DialogMaxHeight** (double): Altura máxima do modal em pixels (padrão: 700)

## Eventos

- **Closed**: Disparado quando o modal é fechado (botão X ou programaticamente)

## Métodos

- **SetContent(UIElement content)**: Define o conteúdo do modal
- **Close()**: Fecha o modal programaticamente

## Exemplo de Uso no XAML

```xaml
<local:ModalDialog Title="Configurações Avançadas" DialogMaxWidth="600" DialogMaxHeight="700">
    <StackPanel Margin="20">
        <TextBlock Text="Seu conteúdo aqui" FontSize="14" Foreground="White"/>
        
        <!-- Seus controles aqui -->
        <CheckBox Content="Opção 1" Foreground="White"/>
        <CheckBox Content="Opção 2" Foreground="White"/>
        
        <!-- Botões de ação -->
        <Grid Margin="0,10,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="10"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            
            <Button Content="Cancelar" Grid.Column="0" Click="BtnCancel_Click"/>
            <Button Content="Salvar" Grid.Column="2" Click="BtnSave_Click"/>
        </Grid>
    </StackPanel>
</local:ModalDialog>
```

## Exemplo de Uso no Code-Behind

```csharp
// Criar modal com conteúdo
var modal = new ModalDialog 
{ 
    Title = "Configurações Avançadas",
    DialogMaxWidth = 600,
    DialogMaxHeight = 700
};

// Criar conteúdo
var contentPanel = new StackPanel { Margin = new Thickness(20) };
contentPanel.Children.Add(new TextBlock { Text = "Seu conteúdo aqui", FontSize = 14, Foreground = Brushes.White });

// Adicionar controles
var checkBox1 = new CheckBox { Content = "Opção 1", Foreground = Brushes.White };
var checkBox2 = new CheckBox { Content = "Opção 2", Foreground = Brushes.White };
contentPanel.Children.Add(checkBox1);
contentPanel.Children.Add(checkBox2);

// Adicionar botões
var buttonPanel = new Grid { Margin = new Thickness(0, 10, 0, 0) };
buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
buttonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

var btnCancel = new Button { Content = "Cancelar", Grid.Column = 0 };
var btnSave = new Button { Content = "Salvar", Grid.Column = 2 };
buttonPanel.Children.Add(btnCancel);
buttonPanel.Children.Add(btnSave);

contentPanel.Children.Add(buttonPanel);

// Definir conteúdo
modal.SetContent(contentPanel);

// Assinar evento de fechamento
modal.Closed += (s, e) => 
{
    // Ação ao fechar
    Logger.Log("Modal fechado");
};

// Navegar para o modal
if (Application.Current.MainWindow is MainWindow mw)
{
    mw.MainFrame.Navigate(modal);
}
```

## Exemplo Completo - Página com Modal

```xaml
<!-- MinhaPagina.xaml -->
<Page x:Class="KitLugia.GUI.Pages.MinhaPagina">
    <StackPanel>
        <Button Content="Abrir Modal" Click="BtnOpenModal_Click"/>
    </StackPanel>
</Page>
```

```csharp
// MinhaPagina.xaml.cs
private void BtnOpenModal_Click(object sender, RoutedEventArgs e)
{
    var modal = new ModalDialog { Title = "Configurações" };
    
    var content = CreateModalContent();
    modal.SetContent(content);
    
    modal.Closed += (s, e) => 
    {
        // Voltar para página anterior
        if (Application.Current.MainWindow is MainWindow mw && mw.MainFrame.CanGoBack)
            mw.MainFrame.GoBack();
    };
    
    if (Application.Current.MainWindow is MainWindow mw)
    {
        mw.MainFrame.Navigate(modal);
    }
}

private UIElement CreateModalContent()
{
    var panel = new StackPanel { Margin = new Thickness(20) };
    // ... criar conteúdo
    return panel;
}
```

## Estilo

O ModalDialog usa:
- **Background**: Overlay semi-transparente (#E0000000)
- **Modal Background**: #1A1A1A
- **Header Background**: #252525
- **Border**: #333 com espessura 1
- **CornerRadius**: 12
- **Botão X**: 36x36px, muda para vermelho no hover
- **ScrollViewer**: Com scroll vertical automático

## Dicas

1. **Conteúdo Grande**: Use DialogMaxHeight para limitar a altura e ativar scroll
2. **Responsivo**: Use DialogMaxWidth para garantir que o modal não fique muito largo
3. **Navegação**: Sempre use `MainFrame.GoBack()` ao fechar para voltar à página anterior
4. **Eventos**: Use o evento `Closed` para limpar recursos ou executar ações ao fechar
5. **Conteúdo Dinâmico**: Use `SetContent()` para definir conteúdo programaticamente

## Exemplo com AdvancedRamCleanSettingsPage

O AdvancedRamCleanSettingsPage usa este padrão. Veja o arquivo para um exemplo completo.

