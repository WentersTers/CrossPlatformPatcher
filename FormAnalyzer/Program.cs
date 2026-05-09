using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Linq;
using System.Collections.Generic;

namespace FormAnalyzer
{
    class Program
    {
        static void Main(string[] args)
        {
            var path = args.Length > 0 ? args[0] : "/Users/sheryluglis/Downloads/CrossPlatformPatcher/PAIcom_Player_Folder/PAIcom_patched.exe";
            
            Console.WriteLine($"Loading: {path}");
            var ctx = ModuleDef.CreateModuleContext();
            var module = ModuleDefMD.Load(path, ctx);
            
            // Find all types that inherit from Form
            var formTypes = new List<TypeDef>();
            foreach (var type in module.GetTypes())
            {
                if (type.BaseType != null && type.BaseType.FullName.Contains("Form"))
                {
                    formTypes.Add(type);
                    Console.WriteLine($"\n{'='*80}");
                    Console.WriteLine($"FORM TYPE FOUND: {type.FullName}");
                    Console.WriteLine($"  BaseType: {type.BaseType.FullName}");
                    Console.WriteLine($"  Methods: {type.Methods.Count}");
                    Console.WriteLine($"  Fields: {type.Fields.Count}");
                    Console.WriteLine($"  Properties: {type.Properties.Count}");
                    Console.WriteLine($"  Has NestedTypes: {type.HasNestedTypes}");
                }
            }
            
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"\nTotal Form types found: {formTypes.Count}");
            
            // For each Form type, analyze methods that take string parameters
            foreach (var formType in formTypes)
            {
                Console.WriteLine($"\n{'='*80}");
                Console.WriteLine($"ANALYZING FORM: {formType.FullName}");
                Console.WriteLine($"{'='*80}");
                
                // Find methods with string parameter
                var stringMethods = formType.Methods.Where(m => 
                    m.HasBody && 
                    m.Parameters.Any(p => p.Type.FullName == "System.String")
                ).ToList();
                
                Console.WriteLine($"\nMethods with string parameter: {stringMethods.Count}");
                
                // Score them by size (largest = likely the command/animation handler)
                var scored = stringMethods.Select(m => new {
                    Method = m,
                    ILSize = m.Body.Instructions.Count,
                    HasPictureBox = m.Body.Instructions.Any(i => 
                        i.Operand?.ToString()?.Contains("PictureBox") == true),
                    HasImage = m.Body.Instructions.Any(i => 
                        i.Operand?.ToString()?.Contains("Image") == true),
                    HasProcess = m.Body.Instructions.Any(i => 
                        i.Operand?.ToString()?.Contains("Process") == true),
                    HasAnimation = m.Body.Instructions.Any(i => 
                        i.Operand?.ToString()?.Contains("anim") == true ||
                        i.Operand?.ToString()?.Contains("Anim") == true),
                    LdstrCount = m.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldstr)
                }).OrderByDescending(x => x.ILSize).ToList();
                
                Console.WriteLine("\nTop 10 largest string-parameter methods:");
                foreach (var item in scored.Take(10))
                {
                    Console.WriteLine($"  {item.Method.Name}: IL={item.ILSize}, Ldstr={item.LdstrCount}, " +
                        $"PictureBox={item.HasPictureBox}, Image={item.HasImage}, " +
                        $"Process={item.HasProcess}, Anim={item.HasAnimation}");
                }
                
                // Find fields that might be PictureBoxes or Image lists
                var pictureBoxFields = formType.Fields.Where(f => 
                    f.FieldType.FullName.Contains("PictureBox")
                ).ToList();
                
                var imageFields = formType.Fields.Where(f => 
                    f.FieldType.FullName.Contains("Image") || 
                    f.FieldType.FullName.Contains("Bitmap") ||
                    f.FieldType.FullName.Contains("ImageList")
                ).ToList();
                
                Console.WriteLine($"\nPictureBox fields: {pictureBoxFields.Count}");
                foreach (var f in pictureBoxFields)
                {
                    Console.WriteLine($"  - {f.Name}: {f.FieldType.FullName}");
                }
                
                Console.WriteLine($"\nImage/Bitmap fields: {imageFields.Count}");
                foreach (var f in imageFields.Take(20))
                {
                    Console.WriteLine($"  - {f.Name}: {f.FieldType.FullName}");
                }
                
                // Find the InitializeComponent method (usually contains animation setup)
                var initMethod = formType.Methods.FirstOrDefault(m => 
                    m.Name.String.Contains("InitializeComponent") ||
                    m.Name.String.Contains("Init")
                );
                
                if (initMethod != null && initMethod.HasBody)
                {
                    Console.WriteLine($"\nInit method found: {initMethod.Name}, IL size: {initMethod.Body.Instructions.Count}");
                    
                    // Look for PictureBox creation/setup
                    var pictureBoxCreations = initMethod.Body.Instructions.Where(i =>
                        i.OpCode == OpCodes.Newobj && 
                        i.Operand?.ToString()?.Contains("PictureBox") == true
                    ).Count();
                    
                    Console.WriteLine($"  PictureBox creations: {pictureBoxCreations}");
                }
                
                // Look for the command handler (largest method with string param)
                if (scored.Count > 0)
                {
                    var largest = scored[0];
                    Console.WriteLine($"\nLikely command handler: {largest.Method.Name}");
                    Console.WriteLine($"  Full name: {largest.Method.FullName}");
                    Console.WriteLine($"  IL Size: {largest.ILSize}");
                    Console.WriteLine($"  Token: {largest.Method.MDToken}");
                    
                    // Extract string literals from this method
                    var strings = largest.Method.Body.Instructions
                        .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
                        .Select(i => (string)i.Operand!)
                        .Distinct()
                        .ToList();
                    
                    Console.WriteLine($"\n  String literals ({strings.Count}):");
                    foreach (var s in strings.Take(30))
                    {
                        Console.WriteLine($"    \"{s}\"");
                    }
                }
            }
            // Find the main Form with most methods
            var mainForm = formTypes.OrderByDescending(f => f.Methods.Count).First();
            Console.WriteLine($"\n{'='*80}");
            Console.WriteLine($"MAIN FORM IDENTIFIED: {mainForm.FullName}");
            Console.WriteLine($"{'='*80}");
            
            // Find the MASSIVE method (likely the animation/command database)
            var massiveMethods = mainForm.Methods.Where(m => 
                m.HasBody && m.Body.Instructions.Count > 1000
            ).OrderByDescending(m => m.Body.Instructions.Count).ToList();
            
            Console.WriteLine($"\nMassive methods (>1000 IL instructions): {massiveMethods.Count}");
            foreach (var method in massiveMethods.Take(5))
            {
                var ilSize = method.Body.Instructions.Count;
                var strings = method.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Ldstr && i.Operand is string)
                    .Select(i => (string)i.Operand!)
                    .Distinct()
                    .ToList();
                
                Console.WriteLine($"\n  Method: {method.Name}");
                Console.WriteLine($"    IL Size: {ilSize}");
                Console.WriteLine($"    Token: {method.MDToken}");
                Console.WriteLine($"    String literals: {strings.Count}");
                
                // Look for animation-related strings
                var animStrings = strings.Where(s => 
                    s.Contains("magic", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("scissor", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("tic", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Red", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Dot", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Red-Dot", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains(".exe", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("files/", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains(".txt", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("Start", StringComparison.OrdinalIgnoreCase)
                ).ToList();
                
                if (animStrings.Count > 0)
                {
                    Console.WriteLine($"    Animation/Game-related strings:");
                    foreach (var s in animStrings.Take(20))
                    {
                        Console.WriteLine($"      \"{s}\"");
                    }
                }
                
                // Count branches (switch statements = command dispatch)
                var branchCount = method.Body.Instructions.Count(i => 
                    i.OpCode.FlowControl == FlowControl.Cond_Branch ||
                    i.OpCode.FlowControl == FlowControl.Branch);
                Console.WriteLine($"    Branches: {branchCount}");
            }
        }
    }
}
