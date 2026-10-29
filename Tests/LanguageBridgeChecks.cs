using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
class Check {
 static int Main(string[] args) {
  string root=Path.GetFullPath(args[0]), plugins=Path.GetFullPath(args[1]);
  AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
   foreach(string d in new[]{plugins,Path.Combine(root,"Beat Saber_Data/Managed"),Path.Combine(root,"Libs"),Path.Combine(root,"Plugins")}) {
    string p=Path.Combine(d,new AssemblyName(e.Name).Name+".dll"); if(File.Exists(p)) return Assembly.LoadFrom(p);
   } return null;
  };
  try {
   var cn=Assembly.LoadFrom(Path.Combine(plugins,"ModUiChinese.dll"));
   var bss=Assembly.LoadFrom(Path.Combine(plugins,"BetterSongSearch.dll"));
   var engineType=cn.GetType("ModUiChinese.TranslationEngine",true);
   var engineField=cn.GetType("ModUiChinese.UiTranslator",true).GetField("Engine");
   var bridge=bss.GetType("BetterSongSearch.Util.BssText",true);
   var translate=bridge.GetMethod("T");
   var catalog=new Dictionary<string,Dictionary<string,string>>{{"BetterSongSearch",new Dictionary<string,string>{{"Default","Custom caption"}}}};
   object[,] cases={{true,null,"Custom caption"},{false,null,"Default"},{true,new[]{"BetterSongSearch"},"Default"},{true,null,"Custom caption"}};
   for(int i=0;i<cases.GetLength(0);i++) {
    engineField.SetValue(null,Activator.CreateInstance(engineType,new object[]{catalog,cases[i,1],cases[i,0]}));
    string actual=(string)translate.Invoke(null,new object[]{"Default"});
    if(actual!=(string)cases[i,2]) throw new Exception("Case "+i+": "+actual);
   }
   bridge.GetMethod("OnLocalizationReloaded").Invoke(null,null);
   Console.WriteLine("BSS compiled language bridge: 5 checks passed"); return 0;
  } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
 }
}
