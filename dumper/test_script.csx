using System; 
using System.Reflection; 
using System.Linq; 

var path = "PAIcom_Player_Folder/PAIcom.exe"; 
var asm = Assembly.LoadFrom(path); 
Type[] types; 
try { types = asm.GetTypes(); } 
catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; } 

var targetType = types.FirstOrDefault(t => t.Name == "D9B+]}FOz6OifCnUpI8ffY^W!");
Console.WriteLine($"Found type: {targetType != null}");
if (targetType != null) {
    foreach (var m in targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
        var paramTypes = m.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        string paramsStr = string.Join(", ", paramTypes);
        Console.WriteLine($"{m.Name} | Returns: {m.ReturnType.Name} | Params: {paramsStr}");
    }
}
