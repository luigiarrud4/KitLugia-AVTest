using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

class Program
{
    static void Main()
    {
        var json = File.ReadAllText("KitLugia_Commands_Export.json");
        var data = JsonSerializer.Deserialize<JsonElement>(json);
        
        var commands = data.GetProperty("commands").EnumerateArray();
        
        var userFriendly = commands.Where(cmd =>
        {
            var isStatic = cmd.GetProperty("isStatic").GetBoolean();
            var paramCount = cmd.GetProperty("parameterCount").GetInt32();
            var visibility = cmd.GetProperty("visibility").GetString();
            var className = cmd.GetProperty("className").GetString();
            
            return isStatic == true 
                && paramCount == 0 
                && visibility == "PUBLIC"
                && className?.StartsWith("<") == false
                && className?.Contains("d__") == false
                && className?.Contains("b__") == false
                && className?.Contains("c__") == false;
        }).Select(cmd => new
        {
            className = cmd.GetProperty("className").GetString(),
            methodName = cmd.GetProperty("methodName").GetString(),
            signature = cmd.GetProperty("signature").GetString(),
            returnType = cmd.GetProperty("returnType").GetString()
        }).OrderBy(c => c.className).ThenBy(c => c.methodName).ToList();
        
        var result = new
        {
            totalCommands = userFriendly.Count,
            commands = userFriendly
        };
        
        var options = new JsonSerializerOptions { WriteIndented = true };
        var output = JsonSerializer.Serialize(result, options);
        File.WriteAllText("UserFriendlyCommands.json", output);
        
        Console.WriteLine($"Encontrados {userFriendly.Count} comandos úteis para o usuário comum");
    }
}
