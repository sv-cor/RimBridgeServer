using Mono.Cecil;
using System.Linq;
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(args[0]));
using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
void MakePublic(TypeDefinition type)
{
    if (type.IsNested) type.IsNestedPublic = true;
    else type.IsPublic = true;
    foreach (var field in type.Fields) field.IsPublic = true;
    foreach (var method in type.Methods) method.IsPublic = true;
    foreach (var nested in type.NestedTypes) MakePublic(nested);
}
foreach (var type in module.Types.Where(type => type.Name != "<Module>")) MakePublic(type);
module.Write(args[1]);

