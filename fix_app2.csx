using System;
using System.Reflection;

class Test
{
    public void CommandTest(string s) {}
    public void NotACommand(string s) {}
}

var m1 = typeof(Test).GetMethod("CommandTest");
var m2 = typeof(Test).GetMethod("NotACommand");

Console.WriteLine(m1.Name);
Console.WriteLine(m2.Name);
