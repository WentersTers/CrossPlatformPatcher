using System;
using System.Reflection;

namespace TestVosk {
    class Program {
        static void Main() {
            try {
                var owPath = @"D:\SteamLibrary\steamapps\common\CrossPlatformPatcher\PAIcom Test Folder\PAIcom.OWW.dll";
                var asm = Assembly.LoadFrom(owPath);
                var type = asm.GetType("CrossPlatformPatcher.PAIcom.OWW.VoskSpeechRecognizer");
                var instance = Activator.CreateInstance(type, new object[] { null });
                var method = type.GetMethod("Initialize");
                
                Environment.SetEnvironmentVariable("PAICOM_MIGRATION_MODE", "full");
                Environment.SetEnvironmentVariable("PAICOM_VOSK_MODEL_PATH", @"D:\SteamLibrary\steamapps\common\CrossPlatformPatcher\PAIcom Test Folder\models\vosk-model-small-en-us-0.15\vosk-model-small-en-us-0.15");
                
                var result = method.Invoke(instance, null);
                Console.WriteLine("Init result: " + result);
            } catch (Exception ex) {
                Console.WriteLine("Error: " + ex);
            }
        }
    }
}
