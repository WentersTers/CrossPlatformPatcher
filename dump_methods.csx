using System;
using System.Reflection;
using System.Linq;

var path = "/Users/sheryluglis/Downloads/CrossPlatformPatcher/PAIcom_Player_Folder/PAIcom_patched.exe";
var asm = Assembly.LoadFrom(path);
var formTypes = asm.GetTypes().Where(t => t.BaseType != null && t.BaseType.FullName == "System.Windows.Forms.Form");

foreach (var t in formTypes)
{
    Console.WriteLine("Form: " + t.FullName);
    var candidates = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(m =>
        {
            if (m.IsSpecialName) return false;
            var parameters = m.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
        });
        
    foreach (var m in candidates)
    {
        var ilLength = m.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
        Console.WriteLine($"  Method: {m.Name}, IL Size: {ilLength}");
    }
}
