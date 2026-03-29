using System;
using System.Linq;

var assemblies = AppDomain.CurrentDomain.GetAssemblies();
Console.WriteLine(assemblies.Length);
