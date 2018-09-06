﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using CsDebugScript.CodeGen.SymbolProviders;
using CsDebugScript.CodeGen.TypeInstances;
using CsDebugScript.CodeGen.UserTypes;
using CsDebugScript.CodeGen.UserTypes.Members;
using CsDebugScript.Engine.Utility;
using DIA;

namespace CsDebugScript.CodeGen.CodeWriters
{
    using UserType = CsDebugScript.CodeGen.UserTypes.UserType;

    /// <summary>
    /// Code writer that outputs IL code and compiles it using System.Reflection.Emit.
    /// </summary>
    internal class ILCodeWriter : DotNetCodeWriter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ILCodeWriter"/> class.
        /// </summary>
        /// <param name="assemblyName">The name of the assembly.</param>
        /// <param name="generationFlags">The code generation options</param>
        /// <param name="nameLimit">Maximum number of characters that generated name can have.</param>
        public ILCodeWriter(string assemblyName, UserTypeGenerationFlags generationFlags, int nameLimit)
            : base(generationFlags, nameLimit)
        {
            AssemblyName = assemblyName;
#if NET461
            templateConstantInterfacesCache = new DictionaryCache<Type, Type[]>(GetITemplateConstantInterface);
#endif
        }

        /// <summary>
        /// Gets the assembly name.
        /// </summary>
        public string AssemblyName { get; private set; }

        /// <summary>
        /// Returns <c>true</c> if code writer supports binary writer.
        /// </summary>
        public override bool HasBinaryWriter => true;

        /// <summary>
        /// Returns <c>true</c> if code writer supports text writer.
        /// </summary>
        public override bool HasTextWriter => false;

        /// <summary>
        /// Generates code for user type and writes it to the specified output.
        /// </summary>
        /// <param name="userType">User type for which code should be generated.</param>
        /// <param name="output">Output text writer.</param>
        public override void WriteUserType(UserType userType, StringBuilder output)
        {
            // This should never be called.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Generated binary code for user types. This is used only if <see cref="HasBinaryWriter"/> is <c>true</c>.
        /// </summary>
        /// <param name="userTypes">User types for which code should be generated.</param>
        /// <param name="dllFileName">Output DLL file path.</param>
        /// <param name="generatePdb"><c>true</c> if PDB file should be generated.</param>
        public override void GenerateBinary(IEnumerable<UserType> userTypes, string dllFileName, bool generatePdb)
        {
#if NET461
            AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName { Name = AssemblyName }, AssemblyBuilderAccess.Save);
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(AssemblyName, dllFileName);

            // Select top level types that will be exported
            List<UserType> allUserTypes = new List<UserType>();

            foreach (UserType userType in userTypes)
            {
                if (!(userType is TemplateArgumentConstantUserType || userType is GlobalsUserType))
                    if (userType is NamespaceUserType || (userType.DeclaredInType != null && !((userType.DeclaredInType is NamespaceUserType) && userType.DeclaredInType.DeclaredInType == null)))
                        continue;

                allUserTypes.Add(userType);
            }

            // Enhence all user types with nested types
            List<UserType> userTypesToProcess = allUserTypes;

            while (userTypesToProcess.Count > 0)
            {
                List<UserType> nextTypes = new List<UserType>();

                foreach (UserType userType in userTypesToProcess)
                {
                    List<UserType> nestedTypes = (userType as TemplateUserType)?.SpecializedRepresentative?.InnerTypes ?? userType.InnerTypes;

                    foreach (var nestedType in nestedTypes)
                    {
                        if (nestedType is SpecializedTemplateUserType)
                        {
                            // Do nothing...
                            // Printing this type comes from updating specialized template user type with declared in type
                            // so declared in type get instances of specialized template user type and we just shouldn't print them.
                            continue;
                        }

                        nextTypes.Add(nestedType);
                    }
                }
                userTypesToProcess = nextTypes;
                allUserTypes.AddRange(nextTypes);
            }

            // Build list that will follow dependency graph (types that are not dependend on any other type should be at the begining of the list).
            GeneratedTypes generatedTypes = new GeneratedTypes(this, moduleBuilder);
            DependencyGraph dependencyGraph = new DependencyGraph(allUserTypes, generatedTypes);

            // Make sure that we generate stubs for all types
            foreach (UserType userType in dependencyGraph.OrderedUserTypes)
                generatedTypes.GetGeneratedType(userType);

            foreach (UserType userType in dependencyGraph.OrderedUserTypes)
            {
                // For types that are only stubs, generated type fully
                if (!(userType is EnumUserType) && !(userType is TemplateArgumentConstantUserType) && !(userType is NamespaceUserType))
                    GenerateType(userType, generatedTypes);

                // Create type if it wasn't already created
                GeneratedType type = generatedTypes.GetGeneratedType(userType);

                if (!type.Created)
                    type.Create();
            }

            assemblyBuilder.Save(dllFileName);
#else
            throw new NotImplementedException();
#endif
        }

#if NET461
        /// <summary>
        /// Helper class that represents generated type. It caries all info related to generated types,
        /// especially info about types that are still not created.
        /// </summary>
        private class GeneratedType
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="GeneratedType"/> class.
            /// </summary>
            public GeneratedType()
            {
                DefinedFields = new DictionaryCache<string, FieldBuilder>(DefineField);
                DefinedConstructors = new DictionaryCache<UserTypeConstructor, ConstructorBuilder>(DefineConstructor);
            }

            /// <summary>
            /// Gets the enumeration builder. It is <c>null</c> if generated type is using <see cref="TypeBuilder"/>.
            /// </summary>
            public EnumBuilder EnumBuilder { get; private set; }

            /// <summary>
            /// Gets the type builder. It is <c>null</c> if generated type is using <see cref="EnumBuilder"/>.
            /// </summary>
            public TypeBuilder TypeBuilder { get; private set; }

            /// <summary>
            /// Gets the defined constructors. If constructor is not defined, it will be created.
            /// </summary>
            public DictionaryCache<UserTypeConstructor, ConstructorBuilder> DefinedConstructors { get; private set; }

            /// <summary>
            /// Gets the defined fields. If field is not defined, it will be created. This is true only for built-in fields.
            /// </summary>
            public DictionaryCache<string, FieldBuilder> DefinedFields { get; private set; }

            /// <summary>
            /// Gets the defined nested types.
            /// </summary>
            public Dictionary<string, GeneratedType> DefinedNestedTypes { get; private set; } = new Dictionary<string, GeneratedType>();

            /// <summary>
            /// Gets the dependent types. Used in <see cref="DependencyGraph"/>.
            /// </summary>
            public HashSet<GeneratedType> DependentTypes { get; private set; } = new HashSet<GeneratedType>();

            /// <summary>
            /// Gets the created type. Not null only when type is created. <see cref="Created"/>.
            /// </summary>
            public Type CreatedType { get; private set; }

            /// <summary>
            /// Returns <c>true</c> if type has been created. <see cref="Create()"/>
            /// </summary>
            public bool Created => CreatedType != null;

            /// <summary>
            /// Gets the associated type (either from <see cref="TypeBuilder"/> or <see cref="EnumBuilder"/>).
            /// </summary>
            public Type Type => TypeBuilder?.AsType() ?? EnumBuilder?.AsType();

            /// <summary>
            /// Creates generated type from the specified enumeration builder.
            /// </summary>
            /// <param name="enumBuilder">The enumeration builder.</param>
            public static GeneratedType Create(EnumBuilder enumBuilder)
            {
                return new GeneratedType
                {
                    EnumBuilder = enumBuilder,
                };
            }

            /// <summary>
            /// Creates generated type from the specified type builder.
            /// </summary>
            /// <param name="typeBuilder">The type builder.</param>
            public static GeneratedType Create(TypeBuilder typeBuilder)
            {
                return new GeneratedType
                {
                    TypeBuilder = typeBuilder,
                };
            }

            /// <summary>
            /// Creates type if it is not already created.
            /// </summary>
            public void Create()
            {
                if (!Created)
                    CreatedType = TypeBuilder?.CreateType() ?? EnumBuilder?.CreateType();
            }

            /// <summary>
            /// Defines field that is not yet defines. It works only for built-in fields.
            /// </summary>
            /// <param name="name">The known field name.</param>
            private FieldBuilder DefineField(string name)
            {
                if (name == ThisClassFieldName)
                    return TypeBuilder.DefineField(ThisClassFieldName, Defines.UserMember_Variable, FieldAttributes.Private);
                if (name == BaseClassStringFieldName)
                    return TypeBuilder.DefineField(BaseClassStringFieldName, Defines.String, FieldAttributes.Private | FieldAttributes.Static);
                if (name == ClassCodeTypeFieldName)
                    return TypeBuilder.DefineField(ClassCodeTypeFieldName, Defines.CodeType, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);

                throw new NotImplementedException();
            }

            /// <summary>
            /// Defines constructor that is not yet defined.
            /// </summary>
            /// <param name="constructor">The user type constructor.</param>
            private ConstructorBuilder DefineConstructor(UserTypeConstructor constructor)
            {
                Type[] parameters = constructor.Arguments.Select(a => a.Item1).ToArray();
                ConstructorBuilder constructorBuilder = TypeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameters);

                for (int i = 0; i < constructor.Arguments.Length; i++)
                {
                    var argument = constructor.Arguments[i];
                    object defaultValue;
                    if (constructor.DefaultValues != null && constructor.DefaultValues.TryGetValue(argument.Item2, out defaultValue))
                    {
                        ParameterBuilder parameterBuilder = constructorBuilder.DefineParameter(i + 1, ParameterAttributes.HasDefault, argument.Item2);
                        parameterBuilder.SetConstant(defaultValue);
                    }
                    else
                        constructorBuilder.DefineParameter(i + 1, ParameterAttributes.None, argument.Item2);
                }

                return constructorBuilder;
            }
        }

        /// <summary>
        /// Helper class that represents collection of generated types.
        /// </summary>
        private class GeneratedTypes : ITypeConverter
        {
            /// <summary>
            /// Dictionary of associated generated types for the specified user type.
            /// </summary>
            private Dictionary<UserType, GeneratedType> types;

            /// <summary>
            /// Initializes a new instance of the <see cref="GeneratedTypes"/> class.
            /// </summary>
            /// <param name="codeWriter">The IL code writer.</param>
            /// <param name="moduleBuilder">The module builder.</param>
            public GeneratedTypes(ILCodeWriter codeWriter, ModuleBuilder moduleBuilder)
            {
                CodeWriter = codeWriter;
                ModuleBuilder = moduleBuilder;
                types = new Dictionary<UserType, GeneratedType>();
            }

            /// <summary>
            /// Gets the IL code writer.
            /// </summary>
            public ILCodeWriter CodeWriter { get; private set; }

            /// <summary>
            /// Gets the module builder.
            /// </summary>
            public ModuleBuilder ModuleBuilder { get; private set; }

            /// <summary>
            /// Gets the all generated types.
            /// </summary>
            public IEnumerable<GeneratedType> Types => types.Values;

            /// <summary>
            /// Gets the generated type for the specified user type.
            /// </summary>
            /// <param name="type">The user type.</param>
            public GeneratedType GetGeneratedType(UserType type)
            {
                GeneratedType generatedType;

                // Check if we already generated type
                if (types.TryGetValue(type, out generatedType))
                    return generatedType;

                // Check if it is template user type
                if (type is SpecializedTemplateUserType templateType && templateType.NumberOfTemplateArguments > 0)
                    return GetGeneratedType(templateType.TemplateType);

                // Check if it is nested user type
                if (type.DeclaredInType == null || ((type.DeclaredInType is NamespaceUserType) && type.DeclaredInType.DeclaredInType == null))
                {
                    // Nope, this is top level user type
                    if (type is EnumUserType enumType)
                        generatedType = GeneratedType.Create(CodeWriter.GenerateType(enumType, ModuleBuilder));
                    else if (type is TemplateArgumentConstantUserType constantType)
                        generatedType = GeneratedType.Create(CodeWriter.GenerateType(constantType, ModuleBuilder, this));
                    else
                        generatedType = GeneratedType.Create(CodeWriter.CreateType(type, ModuleBuilder));
                }
                else
                {
                    // Nested type
                    GeneratedType parentType = GetGeneratedType(type.DeclaredInType);

                    // Don't create new type if type is already created.
                    if (parentType.DefinedNestedTypes.TryGetValue(type.TypeName, out generatedType))
                        return generatedType;

                    if (type is EnumUserType enumType)
                        generatedType = GeneratedType.Create(CodeWriter.GenerateNestedType(enumType, parentType.TypeBuilder));
                    else if (type is NamespaceUserType namespaceType)
                        generatedType = GeneratedType.Create(CodeWriter.GenerateNestedType(namespaceType, parentType.TypeBuilder));
                    else
                        generatedType = GeneratedType.Create(CodeWriter.CreateNestedType(type, parentType.TypeBuilder));
                    parentType.DefinedNestedTypes.Add(type.TypeName, generatedType);
                }

                types.Add(type, generatedType);
                return generatedType;
            }

            /// <summary>
            /// Gets the type for the specified type instance.
            /// </summary>
            /// <param name="typeInstance">The type instance.</param>
            public Type GetType(TypeInstance typeInstance)
            {
                return typeInstance.GetType(this);
            }

        #region ITypeConverter
            /// <summary>
            /// Gets type associated with user type.
            /// </summary>
            /// <param name="userType">The user type.</param>
            public Type GetType(UserType userType)
            {
                GeneratedType generatedType = GetGeneratedType(userType);

                return generatedType.CreatedType ?? generatedType.Type;
            }

            /// <summary>
            /// Gets type generic parameter by parameter name.
            /// </summary>
            /// <param name="userType">The user type</param>
            /// <param name="parameter">Parameter name</param>
            public Type GetGenericParameter(UserType userType, string parameter)
            {
                GeneratedType generatedType = GetGeneratedType(userType);

                return generatedType.TypeBuilder.GenericTypeParameters.FirstOrDefault(p => p.Name == parameter);
            }
        #endregion
        }

        /// <summary>
        /// Helper class that solves dependency graph for specified user types.
        /// </summary>
        private class DependencyGraph
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="DependencyGraph"/> class.
            /// </summary>
            /// <param name="userTypes">The list of all user types.</param>
            /// <param name="generatedTypes">The generated types collection.</param>
            public DependencyGraph(IReadOnlyList<UserType> userTypes, GeneratedTypes generatedTypes)
            {
                GeneratedTypes = generatedTypes;

                // Initialize all DependentTypes for GeneratedTypes
                InitializeDependentTypes(userTypes);

                // Find list of types that respects dependencies...
                FindOrderedUserTypes(userTypes);
            }

            /// <summary>
            /// Gets the generated types collection.
            /// </summary>
            public GeneratedTypes GeneratedTypes { get; private set; }

            /// <summary>
            /// Gets the ordered user types to respect dependency graph.
            /// </summary>
            public List<UserType> OrderedUserTypes { get; private set; }

            /// <summary>
            /// Finds ordered user types list that repsects dependency graph.
            /// </summary>
            /// <param name="userTypes">The list of all user types.</param>
            private void FindOrderedUserTypes(IReadOnlyList<UserType> userTypes)
            {
                GeneratedType[] types = userTypes.Select(ut => GeneratedTypes.GetGeneratedType(ut)).ToArray();
                bool[] selected = new bool[types.Length];
                HashSet<GeneratedType> selectedTypes = new HashSet<GeneratedType>();
                List<UserType> orderedUserTypes = new List<UserType>();

                while (selectedTypes.Count < types.Length)
                {
                    int startingCount = selectedTypes.Count;

                    for (int i = 0; i < types.Length; i++)
                        if (!selected[i])
                        {
                            GeneratedType type = types[i];
                            bool ready = true;

                            foreach (GeneratedType dependency in type.DependentTypes)
                                if (!selectedTypes.Contains(dependency) && type != dependency)
                                {
                                    ready = false;
                                    break;
                                }
                            if (!ready)
                                continue;
                            selected[i] = true;
                            selectedTypes.Add(type);
                            orderedUserTypes.Add(userTypes[i]);
                        }

                    if (startingCount == selectedTypes.Count)
                    {
#if DEBUG
                        Console.WriteLine("Dead-lock in dependency graph");
#endif
                        for (int i = 0; i < types.Length; i++)
                            if (!selected[i])
                                orderedUserTypes.Add(userTypes[i]);
                        break;
                    }
                }
                OrderedUserTypes = orderedUserTypes;
            }

            /// <summary>
            /// Initializes <see cref="GeneratedType.DependentTypes"/> for list of all user types.
            /// </summary>
            /// <param name="userTypes">The list of all user types.</param>
            private void InitializeDependentTypes(IEnumerable<UserType> userTypes)
            {
                foreach (UserType userType in userTypes)
                {
                    GeneratedType generatedType = GeneratedTypes.GetGeneratedType(userType);

                    // Add declared in type
                    if (userType.DeclaredInType != null && !(userType.DeclaredInType is NamespaceUserType && userType.DeclaredInType.DeclaredInType == null))
                        generatedType.DependentTypes.Add(GeneratedTypes.GetGeneratedType(userType.DeclaredInType));

                    if (userType is TemplateArgumentConstantUserType && userType.Symbol is EnumConstantSymbol enumConstant)
                        AddDependentTypes(generatedType, userType.Factory.GetSymbolTypeInstance(userType, enumConstant.EnumSymbol));

                    if (!(userType is TemplateArgumentConstantUserType) && !(userType is NamespaceUserType))
                    {
                        // Add base class
                        AddDependentTypes(generatedType, userType.BaseClass);

                        // Add constant field types
                        foreach (ConstantUserTypeMember member in userType.Members.OfType<ConstantUserTypeMember>())
                            AddDependentTypes(generatedType, member.Type);

                        // Add data field types that are enums
                        foreach (DataFieldUserTypeMember member in userType.Members.OfType<DataFieldUserTypeMember>())
                            if (member.Symbol.Type.Tag == Engine.CodeTypeTag.Enum)
                                AddDependentTypes(generatedType, member.Type);
                    }
                }
            }

            /// <summary>
            /// Adds dependent types to <see cref="GeneratedType.DependentTypes"/> for the specified type instance.
            /// </summary>
            /// <param name="generatedType">The generated type that will have additional dependencies.</param>
            /// <param name="typeInstance">The type instance.</param>
            private void AddDependentTypes(GeneratedType generatedType, TypeInstance typeInstance)
            {
                if (typeInstance is SingleClassInheritanceWithInterfacesTypeInstance singleClassInheritance)
                    typeInstance = singleClassInheritance.BaseClassUserType;

                if (typeInstance is UserTypeInstance userTypeInstance)
                    generatedType.DependentTypes.Add(GeneratedTypes.GetGeneratedType(userTypeInstance.UserType));

                // Check template arguments of base class
                if (typeInstance is TemplateTypeInstance templateTypeInstance)
                {
                    List<TypeInstance> arguments = templateTypeInstance.SpecializedArguments.Where(sa => sa != null).SelectMany(sa => sa).ToList();

                    while (arguments.Count > 0)
                    {
                        List<TypeInstance> nextArguments = new List<TypeInstance>();

                        foreach (TypeInstance argument in arguments)
                        {
                            if (argument is UserTypeInstance argumentUserTypeInstance)
                                generatedType.DependentTypes.Add(GeneratedTypes.GetGeneratedType(argumentUserTypeInstance.UserType));
                            if (argument is TemplateTypeInstance argumentTemplateTypeInstance)
                                nextArguments.AddRange(argumentTemplateTypeInstance.SpecializedArguments.Where(sa => sa != null).SelectMany(sa => sa));
                        }
                        arguments = nextArguments;
                    }
                }
            }
        }

        #region Enumeration
        /// <summary>
        /// Generates enumeration builder for the specified enumeration user type.
        /// </summary>
        /// <param name="type">The enumeration user type.</param>
        /// <param name="moduleBuilder">The module builder</param>
        private EnumBuilder GenerateType(EnumUserType type, ModuleBuilder moduleBuilder)
        {
            Type basicType = type.BasicType ?? Defines.Int;
            EnumBuilder enumBuilder = moduleBuilder.DefineEnum(type.FullTypeName, TypeAttributes.Public, basicType);

            if (type.AreValuesFlags)
                enumBuilder.SetCustomAttribute(new CustomAttributeBuilder(Defines.FlagsAttribute_Constructor, new object[0]));

            foreach (var enumValue in type.Symbol.EnumValues)
            {
                object value;

                if (enumValue.Item2[0] == '-')
                    value = Convert.ChangeType(long.Parse(enumValue.Item2), basicType);
                else
                    value = Convert.ChangeType(ulong.Parse(enumValue.Item2), basicType);
                enumBuilder.DefineLiteral(enumValue.Item1, value);
            }

            return enumBuilder;
        }

        /// <summary>
        /// Generated type builder for the specified nested enumeration user type.
        /// </summary>
        /// <param name="type">The enumeration user type.</param>
        /// <param name="typeBuilder">The parent type builder.</param>
        private TypeBuilder GenerateNestedType(EnumUserType type, TypeBuilder typeBuilder)
        {
            Type basicType = type.BasicType ?? Defines.Int;
            TypeBuilder enumerationBuilder = typeBuilder.DefineNestedType(type.ConstructorName, TypeAttributes.NestedPublic | TypeAttributes.Sealed, Defines.Enum);

            MakeGenericsIfNeeded(enumerationBuilder, type);
            if (type.AreValuesFlags)
                enumerationBuilder.SetCustomAttribute(new CustomAttributeBuilder(Defines.FlagsAttribute_Constructor, new object[0]));

            enumerationBuilder.DefineField("value__", basicType, FieldAttributes.Private | FieldAttributes.SpecialName);
            foreach (var enumValue in type.Symbol.EnumValues)
            {
                object value;

                if (enumValue.Item2[0] == '-')
                    value = Convert.ChangeType(long.Parse(enumValue.Item2), basicType);
                else
                    value = Convert.ChangeType(ulong.Parse(enumValue.Item2), basicType);
                enumerationBuilder.DefineField(enumValue.Item1, enumerationBuilder, FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal).SetConstant(value);
            }
            return enumerationBuilder;
        }
        #endregion

        #region Template argument constant
        /// <summary>
        /// Dictionary cache of template constant interfaces for specified built-in type.
        /// </summary>
        private DictionaryCache<Type, Type[]> templateConstantInterfacesCache;

        /// <summary>
        /// Gets the template constant interfaces for the specified built-in type.
        /// </summary>
        /// <param name="argumentType">The built-in argument type.</param>
        private Type[] GetITemplateConstantInterface(Type argumentType)
        {
            Type interfaceType = typeof(ITemplateConstant<>);

            return new[] { interfaceType.MakeGenericType(argumentType), typeof(ITemplateConstant) };
        }

        /// <summary>
        /// Generated type builder for the speciifed template argument constant.
        /// </summary>
        /// <param name="type">The template argument constant.</param>
        /// <param name="moduleBuilder">The module builder</param>
        /// <param name="generatedTypes">The generated types list.</param>
        private TypeBuilder GenerateType(TemplateArgumentConstantUserType type, ModuleBuilder moduleBuilder, GeneratedTypes generatedTypes)
        {
            IntegralConstantSymbol integralConstant = type.Symbol as IntegralConstantSymbol;
            EnumConstantSymbol enumConstant = type.Symbol as EnumConstantSymbol;

            if (integralConstant == null)
                throw new NotImplementedException();

            Type constantType;
            if (enumConstant == null)
                constantType = integralConstant.Value.GetType();
            else
                constantType = type.Factory.GetSymbolTypeInstance(type, enumConstant.EnumSymbol).GetType(generatedTypes);
            TypeBuilder tb = moduleBuilder.DefineType(type.FullTypeName, TypeAttributes.Public | TypeAttributes.BeforeFieldInit, Defines.Object, templateConstantInterfacesCache[constantType]);

            tb.SetCustomAttribute(new CustomAttributeBuilder(Defines.TemplateConstantAttribute_Constructor, new object[0], Defines.TemplateConstantAttribute_Properties, new[] { integralConstant.Name, integralConstant.Value }));
            MethodBuilder metb = tb.DefineMethod("get_Value", MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual, constantType, null);

            ILGenerator il = metb.GetILGenerator();
            EmitConstant(il, integralConstant.Value);
            il.Emit(OpCodes.Ret);

            PropertyBuilder pb = tb.DefineProperty("Value", PropertyAttributes.None, constantType, null);
            pb.SetGetMethod(metb);

            return tb;
        }
        #endregion

        #region Nested namespace
        /// <summary>
        /// Generates type builder for the specified nested namespace user type.
        /// </summary>
        /// <param name="type">The nested namespace user type.</param>
        /// <param name="typeBuilder">The parent type builder.</param>
        private TypeBuilder GenerateNestedType(NamespaceUserType type, TypeBuilder typeBuilder)
        {
            TypeBuilder newType = typeBuilder.DefineNestedType(type.ConstructorName, TypeAttributes.Class | TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Abstract);

            return MakeGenericsIfNeeded(newType, type);
        }
        #endregion

        #region Regular types
        /// <summary>
        /// Adds generics parameters to type builder if needed (if user type is template type or if user type is nested type of a template type).
        /// </summary>
        /// <param name="typeBuilder">The type builder.</param>
        /// <param name="type">The user type for the specified type builder.</param>
        private TypeBuilder MakeGenericsIfNeeded(TypeBuilder typeBuilder, UserType type)
        {
            TypeBuilder parentType = typeBuilder.DeclaringType as TypeBuilder;
            int parentArguments = parentType?.GenericTypeParameters?.Length ?? 0;

            if (type is TemplateUserType templateType && templateType.SpecializedRepresentative.NumberOfTemplateArguments > 0)
            {
                string[] arguments = new string[parentArguments + templateType.SpecializedRepresentative.NumberOfTemplateArguments];

                for (int i = 0; i < parentArguments; i++)
                    arguments[i] = parentType.GenericTypeParameters[i].Name;
                for (int i = parentArguments; i < arguments.Length; i++)
                    arguments[i] = templateType.SpecializedRepresentative.GetTemplateArgumentName(i - parentArguments);
                typeBuilder.DefineGenericParameters(arguments);
            }
            else if (parentArguments > 0)
            {
                string[] arguments = new string[parentArguments];

                for (int i = 0; i < parentArguments; i++)
                    arguments[i] = parentType.GenericTypeParameters[i].Name;
                typeBuilder.DefineGenericParameters(arguments);
            }
            return typeBuilder;
        }

        /// <summary>
        /// Creates type builder for the specified user type.
        /// </summary>
        /// <param name="type">The user type.</param>
        /// <param name="moduleBuilder">The module builder.</param>
        private TypeBuilder CreateType(UserType type, ModuleBuilder moduleBuilder)
        {
            TypeBuilder newType;
            if (type.BaseClass is StaticClassTypeInstance)
            {
                newType = moduleBuilder.DefineType(type.FullTypeName, TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
                return MakeGenericsIfNeeded(newType, type);
            }

            string fullConstructorName = type.FullTypeName.Substring(0, type.FullTypeName.Length - type.TypeName.Length) + type.ConstructorName;
            int genericsArguments = (type as TemplateUserType)?.SpecializedRepresentative?.NumberOfTemplateArguments ?? 0;
            newType = moduleBuilder.DefineType(genericsArguments > 0 ? $"{fullConstructorName}`{genericsArguments}" : fullConstructorName, TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.BeforeFieldInit);
            return MakeGenericsIfNeeded(newType, type);
        }

        /// <summary>
        /// Creates type builder for the specified nested user type.
        /// </summary>
        /// <param name="type">The nested user type.</param>
        /// <param name="typeBuilder">The parent type builder.</param>
        private TypeBuilder CreateNestedType(UserType type, TypeBuilder typeBuilder)
        {
            TypeBuilder newType;
            if (type.BaseClass is StaticClassTypeInstance)
            {
                newType = typeBuilder.DefineNestedType(type.ConstructorName, TypeAttributes.Class | TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Abstract);
                return MakeGenericsIfNeeded(newType, type);
            }
            int genericsArguments = (type as TemplateUserType)?.SpecializedRepresentative?.NumberOfTemplateArguments ?? 0;
            newType = typeBuilder.DefineNestedType(genericsArguments > 0 ? $"{type.ConstructorName}`{genericsArguments}" : type.ConstructorName, TypeAttributes.Class | TypeAttributes.NestedPublic | TypeAttributes.BeforeFieldInit);
            return MakeGenericsIfNeeded(newType, type);
        }

        /// <summary>
        /// The number of base class arrays created during code generation.
        /// Used as unique number for next base class array name.
        /// </summary>
        private static long baseClassArraysCreated = 0;

        /// <summary>
        /// Generated type (fields, properties, methods, constructors, constants, etc.) for the specified user type.
        /// </summary>
        /// <param name="type">The user type.</param>
        /// <param name="generatedTypes">The generated types collection.</param>
        private void GenerateType(UserType type, GeneratedTypes generatedTypes)
        {
            bool cacheUserTypeFields = GenerationFlags.HasFlag(UserTypeGenerationFlags.CacheUserTypeFields);
            bool lazyCacheUserTypeFields = GenerationFlags.HasFlag(UserTypeGenerationFlags.LazyCacheUserTypeFields);
            bool useDirectClassAccess = GenerationFlags.HasFlag(UserTypeGenerationFlags.UseDirectClassAccess);
            string baseClassesArrayName = null;
            Type baseClassType = null;
            GeneratedType generatedType = generatedTypes.GetGeneratedType(type);
            TypeBuilder typeBuilder = generatedType.TypeBuilder;
            List<Action<ILGenerator>> staticFieldInitializations = new List<Action<ILGenerator>>();
            List<Action<ILGenerator>> constructorInitializations = new List<Action<ILGenerator>>();
            Type typeReference = !typeBuilder.ContainsGenericParameters ? typeBuilder : typeBuilder.MakeGenericType(typeBuilder.GenericTypeParameters);

            // Update parent type and interfaces
            if (!(type.BaseClass is StaticClassTypeInstance))
            {
                // Write derived class attribute
                if (type.Symbol.HasVTable())
                {
                    foreach (UserType derivedClass in type.DerivedClasses)
                    {
                        // Since user type can be specialized template type (or one of its nested types), we need to create type instance.
                        TypeInstance derivedClassTypeInstance = UserTypeInstance.Create(derivedClass, type.Factory);
                        Type derivedClassType = generatedTypes.GetType(derivedClassTypeInstance);
                        object[] propertyValues = new object[]
                        {
                            derivedClassType,
                            derivedClass.DerivedClasses.Count,
                            derivedClass.Symbol.Name,
                        };
                        CustomAttributeBuilder cab = new CustomAttributeBuilder(Defines.DerivedClassAttribute_Constructor, new object[0], Defines.DerivedClassAttribute_Properties, propertyValues);
                        typeBuilder.SetCustomAttribute(cab);
                    }
                }

                // Write all UserTypeAttributes and class header
                if (type is TemplateUserType templateType)
                {
                    foreach (var specialization in templateType.Specializations)
                        foreach (var moduleName in GlobalCache.GetSymbolModuleNames(specialization.Symbol))
                        {
                            object[] propertyValues = new object[]
                            {
                                moduleName,
                                specialization.Symbol.Name,
                            };
                            CustomAttributeBuilder cab = new CustomAttributeBuilder(Defines.UserTypeAttribute_Constructor, new object[0], Defines.UserTypeAttribute_Properties, propertyValues);
                            typeBuilder.SetCustomAttribute(cab);
                        }
                }
                else
                    foreach (var moduleName in GlobalCache.GetSymbolModuleNames(type.Symbol))
                    {
                        object[] propertyValues = new object[]
                        {
                            moduleName,
                            type.Symbol.Name,
                        };
                        CustomAttributeBuilder cab = new CustomAttributeBuilder(Defines.UserTypeAttribute_Constructor, new object[0], Defines.UserTypeAttribute_Properties, propertyValues);
                        typeBuilder.SetCustomAttribute(cab);
                    }

                // If we have multi class inheritance, generate attribute for getting static field with base class C# types
                if (type.BaseClass is MultiClassInheritanceTypeInstance || type.BaseClass is SingleClassInheritanceWithInterfacesTypeInstance)
                {
                    baseClassesArrayName = $"RandomlyNamed_BaseClassesArray{System.Threading.Interlocked.Increment(ref baseClassArraysCreated)}";

                    object[] propertyValues = new object[]
                    {
                        baseClassesArrayName,
                    };
                    CustomAttributeBuilder cab = new CustomAttributeBuilder(Defines.BaseClassesArrayAttribute_Constructor, new object[0], Defines.BaseClassesArrayAttribute_Properties, propertyValues);
                    typeBuilder.SetCustomAttribute(cab);
                }

                // Update parent type
                baseClassType = generatedTypes.GetType(type.BaseClass);
                typeBuilder.SetParent(baseClassType);
                if (type.Symbol.HasVTable())
                    typeBuilder.AddInterfaceImplementation(Defines.ICastableObject);
            }

            // Write ClassCodeType
            FieldBuilder classCodeTypeField = null;

            if (type is PhysicalUserType || (type is TemplateUserType && type.Members.OfType<DataFieldUserTypeMember>().Any(m => m.IsStatic)))
            {
                classCodeTypeField = generatedType.DefinedFields[ClassCodeTypeFieldName];
                staticFieldInitializations.Add((il) =>
                {
                    il.Emit(OpCodes.Ldtoken, typeReference);
                    il.Emit(OpCodes.Call, Defines.Type_GetTypeFromHandle);
                    il.Emit(OpCodes.Call, Defines.UserType_GetClassCodeType);
                    il.Emit(OpCodes.Stsfld, classCodeTypeField);
                });
            }

            // Write members that are constants
            foreach (var member in type.Members.OfType<ConstantUserTypeMember>())
                if (!(member.Type is TemplateArgumentTypeInstance))
                {
                    Action<ILGenerator, FieldBuilder> staticReadOnlyInitialization;
                    object value = ConstantValue(member, generatedTypes, out staticReadOnlyInitialization);
                    FieldBuilder fieldBuilder = typeBuilder.DefineField(member.Name, generatedTypes.GetType(member.Type), FieldAttributes.Public | FieldAttributes.Static | (staticReadOnlyInitialization == null ? FieldAttributes.Literal : FieldAttributes.HasDefault));

                    generatedType.DefinedFields[fieldBuilder.Name] = fieldBuilder;
                    if (staticReadOnlyInitialization == null)
                        fieldBuilder.SetConstant(value);
                    else
                        staticFieldInitializations.Add((il) => staticReadOnlyInitialization(il, fieldBuilder));
                }

            // Write private class initialization
            FieldBuilder baseClassStringField = null;
            FieldBuilder thisClassField = null;

            bool hasDataFields = type.BaseClass is MultiClassInheritanceTypeInstance
                || type.BaseClass is SingleClassInheritanceWithInterfacesTypeInstance
                || type.Constructors.Contains(UserTypeConstructor.SimplePhysical);
            bool usesThisClass = type.BaseClass is MultiClassInheritanceTypeInstance
                || type.BaseClass is SingleClassInheritanceWithInterfacesTypeInstance;

            foreach (var dataField in type.Members.OfType<DataFieldUserTypeMember>())
            {
                if (dataField.IsStatic)
                    continue;
                hasDataFields = true;

                if (IsDataFieldPropertyUsingThisClassField(type, dataField))
                {
                    usesThisClass = true;
                    break;
                }
            }
            if (!(type.BaseClass is StaticClassTypeInstance))
                if (hasDataFields)
                {
                    baseClassStringField = generatedType.DefinedFields[BaseClassStringFieldName];
                    staticFieldInitializations.Add((il) =>
                    {
                        il.Emit(OpCodes.Ldtoken, typeReference);
                        il.Emit(OpCodes.Call, Defines.Type_GetTypeFromHandle);
                        il.Emit(OpCodes.Call, Defines.UserType_GetBaseClassString);
                        il.Emit(OpCodes.Stsfld, baseClassStringField);
                    });
                    if (usesThisClass)
                    {
                        thisClassField = generatedType.DefinedFields[ThisClassFieldName];

                        // Create method implementation of lambda
                        MethodBuilder thisClassFieldLambda = typeBuilder.DefineMethod($"{ThisClassFieldName}_Lambda", MethodAttributes.Private | MethodAttributes.HideBySig, Defines.Variable, null);
                        ILGenerator mil = thisClassFieldLambda.GetILGenerator();
                        mil.Emit(OpCodes.Ldarg_0);
                        mil.Emit(OpCodes.Ldsfld, baseClassStringField);
                        mil.Emit(OpCodes.Call, Defines.Variable_GetBaseClass_String);
                        mil.Emit(OpCodes.Ret);

                        // Add constructor initialization
                        constructorInitializations.Add((il) =>
                        {
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldftn, thisClassFieldLambda);
                            il.Emit(OpCodes.Newobj, Defines.Func_Variable_Constructor);
                            il.Emit(OpCodes.Call, Defines.UserMember_Create);
                            il.Emit(OpCodes.Stfld, thisClassField);
                        });
                    }
                }

            // Write properties for data fields
            Dictionary<string, PropertyBuilder> generatedProperties = new Dictionary<string, PropertyBuilder>();

            foreach (var dataField in type.Members.OfType<DataFieldUserTypeMember>())
            {
                // Fix array field type if needed, before we create this field
                IsPhysicalDataFieldProperty(type, dataField);

                Type dataFieldType = generatedTypes.GetType(dataField.Type);

                if (!dataField.IsStatic)
                {
                    if (cacheUserTypeFields)
                    {
                        // Create field that will cache value
                        FieldBuilder fieldBuilder = typeBuilder.DefineField(GetUserTypeFieldName(dataField.Name), dataFieldType, FieldAttributes.Private);

                        constructorInitializations.Add((fil) =>
                        {
                            fil.Emit(OpCodes.Ldarg_0);
                            WriteDataFieldPropertyCode(fil, type, dataField, generatedTypes);
                            fil.Emit(OpCodes.Stfld, fieldBuilder);
                        });

                        // Create property get method
                        MethodBuilder propertyMethod = typeBuilder.DefineMethod($"get_{dataField.Name}", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, dataFieldType, null);
                        ILGenerator il = propertyMethod.GetILGenerator();

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, fieldBuilder);
                        il.Emit(OpCodes.Ret);

                        // Create property
                        PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(dataField.Name, PropertyAttributes.None, dataFieldType, null);
                        propertyBuilder.SetGetMethod(propertyMethod);
                        generatedProperties.Add(dataField.Name, propertyBuilder);
                    }
                    else if (lazyCacheUserTypeFields)
                    {
                        // Create method that will be field lambda
                        MethodBuilder fieldMethod = typeBuilder.DefineMethod($"{GetUserTypeFieldName(dataField.Name)}_Lambda", MethodAttributes.Private | MethodAttributes.HideBySig, Defines.Variable, null);
                        ILGenerator mil = fieldMethod.GetILGenerator();
                        WriteDataFieldPropertyCode(mil, type, dataField, generatedTypes);
                        mil.Emit(OpCodes.Ret);

                        // Create field that will cache value
                        FieldBuilder fieldBuilder = typeBuilder.DefineField(GetUserTypeFieldName(dataField.Name), Defines.UserMember_.MakeGenericType(dataFieldType), FieldAttributes.Private);

                        constructorInitializations.Add((fil) =>
                        {
                            fil.Emit(OpCodes.Ldarg_0);
                            fil.Emit(OpCodes.Ldarg_0);
                            fil.Emit(OpCodes.Ldftn, fieldMethod);
                            fil.Emit(OpCodes.Newobj, Defines.Func_.MakeGenericType(dataFieldType).GetConstructor(new Type[] { typeof(object), typeof(IntPtr) }));
                            fil.Emit(OpCodes.Call, Defines.UserMember_Create);
                            fil.Emit(OpCodes.Stfld, fieldBuilder);
                        });

                        // Create property get method
                        MethodBuilder propertyMethod = typeBuilder.DefineMethod($"get_{dataField.Name}", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, dataFieldType, null);
                        ILGenerator il = propertyMethod.GetILGenerator();

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldflda, fieldBuilder);
                        il.Emit(OpCodes.Call, Defines.UserMember_.MakeGenericType(dataFieldType).GetProperty("Value").GetMethod);
                        il.Emit(OpCodes.Ret);

                        // Create property
                        PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(dataField.Name, PropertyAttributes.None, dataFieldType, null);
                        propertyBuilder.SetGetMethod(propertyMethod);
                        generatedProperties.Add(dataField.Name, propertyBuilder);
                    }
                    else
                    {
                        // Create property get method
                        MethodBuilder propertyMethod = typeBuilder.DefineMethod($"get_{dataField.Name}", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, dataFieldType, null);
                        ILGenerator il = propertyMethod.GetILGenerator();

                        WriteDataFieldPropertyCode(il, type, dataField, generatedTypes);
                        il.Emit(OpCodes.Ret);

                        // Create property
                        PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(dataField.Name, PropertyAttributes.None, dataFieldType, null);
                        propertyBuilder.SetGetMethod(propertyMethod);
                        generatedProperties.Add(dataField.Name, propertyBuilder);
                    }
                }
                else
                {
                    MethodBuilder propertyMethod = typeBuilder.DefineMethod($"get_{dataField.Name}", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Static, dataFieldType, null);
                    ILGenerator il = propertyMethod.GetILGenerator();

                    if (type is TemplateUserType)
                    {
                        // ClassCodeType.GetStaticField(dataField.Name);
                        il.Emit(OpCodes.Ldsfld, classCodeTypeField);
                        il.Emit(OpCodes.Ldstr, dataField.Name);
                        il.Emit(OpCodes.Callvirt, Defines.CodeType_GetStaticField);
                    }
                    else
                    {
                        string globalVariableName;

                        if (string.IsNullOrEmpty(type.Symbol.Name))
                            globalVariableName = $"{type.Module.Name}!{dataField.Name}";
                        else
                            globalVariableName = $"{type.Module.Name}!{type.Symbol.Name}::{dataField.Name}";

                        // Process.Current.GetGlobal(globalVariableName);
                        il.Emit(OpCodes.Call, Defines.Process_Current.GetMethod);
                        il.Emit(OpCodes.Ldstr, globalVariableName);
                        il.Emit(OpCodes.Callvirt, Defines.Process_GetGlobal);
                    }

                    CastVariableToDataFieldType(il, dataField, generatedTypes);
                    il.Emit(OpCodes.Ret);

                    PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(dataField.Name, PropertyAttributes.None, dataFieldType, null);
                    propertyBuilder.SetGetMethod(propertyMethod);
                    generatedProperties.Add(dataField.Name, propertyBuilder);
                }
            }

            // Write properties for Hungarian notation generated properties
            foreach (var dataField in type.Members.OfType<HungarianArrayUserTypeMember>())
            {
                Type dataFieldType = generatedTypes.GetType(dataField.Type);
                MethodBuilder propertyMethod = typeBuilder.DefineMethod($"get_{dataField.Name}", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, dataFieldType, null);
                ILGenerator il = propertyMethod.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, generatedProperties[dataField.PointerFieldName].GetMethod);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, generatedProperties[dataField.CounterFieldName].GetMethod);
                il.Emit(OpCodes.Newobj, dataFieldType.GetConstructor(new Type[] { Defines.Variable, generatedProperties[dataField.CounterFieldName].PropertyType }));
                il.Emit(OpCodes.Ret);

                PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(dataField.Name, PropertyAttributes.None, dataFieldType, null);
                propertyBuilder.SetGetMethod(propertyMethod);
                generatedProperties.Add(dataField.Name, propertyBuilder);
            }

            // Write properties for getting base classes
            if (type.BaseClass is MultiClassInheritanceTypeInstance || type.BaseClass is SingleClassInheritanceWithInterfacesTypeInstance)
            {
                BaseClassPropertyUserTypeMember[] baseClassProperties = type.Members.OfType<BaseClassPropertyUserTypeMember>().OrderBy(b => b.Index).ToArray();

                foreach (BaseClassPropertyUserTypeMember baseClassProperty in baseClassProperties)
                {
                    Type propertyType = generatedTypes.GetType(baseClassProperty.Type);
                    MethodBuilder propertyGetMethod = typeBuilder.DefineMethod($"get_{baseClassProperty.Name}", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, propertyType, null);
                    ILGenerator il = propertyGetMethod.GetILGenerator();

                    if (GenerationFlags.HasFlag(UserTypeGenerationFlags.UseDirectClassAccess))
                    {
                        // Load thisClass.Value on the stack
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldflda, thisClassField);
                        il.Emit(OpCodes.Call, thisClassField.FieldType.GetProperty("Value").GetGetMethod());

                        // Call Variable.GetBaseClass<T, TParent>(index, this);
                        EmitConstant(il, baseClassProperty.Index);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetBaseClass_Int_This_.MakeGenericMethod(propertyType, typeReference));
                    }
                    else
                    {
                        // Call Variable.GetBaseClass("symbol name");
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldstr, baseClassProperty.Symbol.Name);
                        il.Emit(OpCodes.Call, Defines.Variable_GetBaseClass_String);

                        // Call Variable.CastAs<T>
                        il.Emit(OpCodes.Callvirt, Defines.Variable_CastAs_.MakeGenericMethod(propertyType));
                    }
                    il.Emit(OpCodes.Ret);

                    PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(baseClassProperty.Name, PropertyAttributes.None, propertyType, null);
                    propertyBuilder.SetGetMethod(propertyGetMethod);
                }

                FieldBuilder baseClassedArrayField = typeBuilder.DefineField(baseClassesArrayName, Defines.TypeArray, FieldAttributes.Public | FieldAttributes.Static);
                generatedType.DefinedFields[baseClassedArrayField.Name] = baseClassedArrayField;

                staticFieldInitializations.Add((il) =>
                {
                    EmitConstant(il, baseClassProperties.Length);
                    il.Emit(OpCodes.Newarr, Defines.Type);
                    for (int i = 0; i < baseClassProperties.Length; i++)
                    {
                        Type propertyType = generatedTypes.GetType(baseClassProperties[i].Type);
                        il.Emit(OpCodes.Dup);
                        EmitConstant(il, i);
                        il.Emit(OpCodes.Ldtoken, propertyType);
                        il.Emit(OpCodes.Call, Defines.Type_GetTypeFromHandle);
                        il.Emit(OpCodes.Stelem_Ref);
                    }
                    il.Emit(OpCodes.Stsfld, baseClassedArrayField);
                });
            }

            // Write code for constructors
            int baseClassOffset = type.BaseClassOffset;
            ConstructorBuilder regularPhysicalConstructor = null;

            foreach (var constructor in type.Constructors)
            {
                if (constructor.IsStatic)
                {
                    // Do nothing. We will add static constructor at the end if there is something to be initialized.
                }
                else
                {
                    Type[] parameters = constructor.Arguments.Select(a => a.Item1).ToArray();
                    ConstructorBuilder constructorBuilder = generatedType.DefinedConstructors[constructor];
                    ILGenerator il = constructorBuilder.GetILGenerator();
                    Func<ConstructorInfo> getBaseClassConstructor = () =>
                    {
                        if (type.BaseClass is UserTypeInstance userTypeInstance)
                        {
                            GeneratedType baseClassData = generatedTypes.GetGeneratedType(userTypeInstance.UserType);

                            if (!baseClassData.Created)
                                return baseClassData.DefinedConstructors[constructor];

                            foreach (ConstructorBuilder cb in baseClassData.DefinedConstructors.Values)
                            {
                                if (constructor.Arguments.Length == cb.GetParameters().Length)
                                {
                                    bool same = true;

                                    for (int i = 0; same && i < constructor.Arguments.Length; i++)
                                        same = constructor.Arguments[i].Item1 == cb.GetParameters()[i].ParameterType;
                                    if (same)
                                        return cb;
                                }
                            }
                        }

                        return baseClassType.GetConstructor(parameters);
                    };

                    if (constructor == UserTypeConstructor.Simple)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Call, getBaseClassConstructor());
                        foreach (var initialization in constructorInitializations)
                            initialization(il);
                        il.Emit(OpCodes.Ret);
                    }
                    else if (constructor == UserTypeConstructor.RegularPhysical)
                    {
                        regularPhysicalConstructor = constructorBuilder;

                        // base(variable, buffer, offset, bufferAddress)
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Ldarg_S, 4);
                        il.Emit(OpCodes.Call, getBaseClassConstructor());
                        foreach (var initialization in constructorInitializations)
                            initialization(il);
                        il.Emit(OpCodes.Ret);
                    }
                    else if (constructor == UserTypeConstructor.ComplexPhysical)
                    {
                        // base(buffer, offset, bufferAddress, codeType, address, name, path)
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Ldarg_S, 4);
                        il.Emit(OpCodes.Ldarg_S, 5);
                        il.Emit(OpCodes.Ldarg_S, 6);
                        il.Emit(OpCodes.Ldarg_S, 7);
                        il.Emit(OpCodes.Call, getBaseClassConstructor());
                        foreach (var initialization in constructorInitializations)
                            initialization(il);
                        il.Emit(OpCodes.Ret);
                    }
                    else if (constructor == UserTypeConstructor.SimplePhysical)
                    {
                        il.Emit(OpCodes.Ldarg_0);

                        // variable.GetBaseClass(baseClassString);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldsfld, baseClassStringField);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetBaseClass_String);

                        // variable.GetCodeType().Module.Process
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetCodeType);
                        il.Emit(OpCodes.Callvirt, Defines.CodeType_Module.GetMethod);
                        il.Emit(OpCodes.Callvirt, Defines.Module_Process.GetMethod);

                        // variable.GetBaseClass(baseClassString).GetPointerAddress()
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldsfld, baseClassStringField);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetBaseClass_String);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetPointerAddress);

                        // variable.GetBaseClass(baseClassString).GetCodeType().Size
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldsfld, baseClassStringField);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetBaseClass_String);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetCodeType);
                        il.Emit(OpCodes.Callvirt, Defines.CodeType_Size.GetMethod);

                        // Debugger.ReadMemory(process, address, size)
                        il.Emit(OpCodes.Call, Defines.Debugger_ReadMemory);

                        // 0
                        EmitConstant(il, 0);

                        // variable.GetBaseClass(baseClassString).GetPointerAddress()
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldsfld, baseClassStringField);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetBaseClass_String);
                        il.Emit(OpCodes.Callvirt, Defines.Variable_GetPointerAddress);

                        // this(variable, buffer, 0, address)
                        il.Emit(OpCodes.Call, regularPhysicalConstructor);
                        il.Emit(OpCodes.Ret);
                    }
                    else
                        throw new NotImplementedException();
                }
            }

            // If some of the static fields are defined, we need to initialize them in static constructor.
            if (staticFieldInitializations.Count > 0)
            {
                ConstructorBuilder staticConstructor = typeBuilder.DefineTypeInitializer();
                ILGenerator il = staticConstructor.GetILGenerator();

                foreach (var initialization in staticFieldInitializations)
                    initialization(il);
                il.Emit(OpCodes.Ret);
            }
        }
        #endregion

        /// <summary>
        /// Checks whether data field can be generated as physical property.
        /// </summary>
        /// <param name="type">The user type containing data field.</param>
        /// <param name="dataField">The data field to be tested.</param>
        /// <returns><c>true</c> if data field can be generated as physical property.</returns>
        private bool IsPhysicalDataFieldProperty(UserType type, DataFieldUserTypeMember dataField)
        {
            // We shouldn't generate physical data field property code if user type is not physical user type.
            // Also, we should ignore static fields.
            PhysicalUserType physicalType = type as PhysicalUserType;

            if (physicalType == null || dataField.IsStatic || type.IsDeclaredInsideTemplate)
                return false;

            SymbolField field = dataField.Symbol;
            int offset = field.Offset - physicalType.MemoryBufferOffset;

            // Specialization for basic type
            if (dataField.Type is BasicTypeInstance basicType)
            {
                if (basicType.BasicType == Defines.String)
                    return true;

                string basicTypeName = ToUserTypeName(basicType.BasicType);

                if (!string.IsNullOrEmpty(basicTypeName))
                    return true;
            }
            // Specialization for arrays
            else if (dataField.Type is ArrayTypeInstance codeArrayType)
            {
                if (codeArrayType.ElementType is BasicTypeInstance basic)
                {
                    string basicTypeName = ToUserTypeName(basic.BasicType);

                    if (!string.IsNullOrEmpty(basicTypeName))
                    {
                        codeArrayType.IsPhysical = true;
                        return true;
                    }
                }
            }
            // Specialication for enum user type
            else if (dataField.Type is EnumTypeInstance enumType)
            {
                string basicTypeName = ToUserTypeName(enumType.EnumUserType.BasicType);

                if (!string.IsNullOrEmpty(basicTypeName))
                    return true;
            }
            // Specialization for user types
            else if (dataField.Type is UserTypeInstance userType)
            {
                if (field.Type.Tag == Engine.CodeTypeTag.Pointer)
                    return true;
                else if (userType.UserType.Constructors.Contains(UserTypeConstructor.ComplexPhysical))
                    return true;
            }
            // Specialization for transformations
            else if (dataField.Type is TransformationTypeInstance transformationType)
            {
                if (field.Type.Tag != Engine.CodeTypeTag.Pointer)
                    return true;
            }

            // We don't know how to specialize this data field. Fall back to original output.
            return false;
        }

        /// <summary>
        /// Checks whether data field is using thicClass field.
        /// </summary>
        /// <param name="type">The user type containing data field.</param>
        /// <param name="dataField">The data field to be tested.</param>
        /// <returns><c>true</c> if data field is using thisClass field.</returns>
        private bool IsDataFieldPropertyUsingThisClassField(UserType type, DataFieldUserTypeMember dataField)
        {
            if (IsPhysicalDataFieldProperty(type, dataField))
                return false;
            if (dataField.IsStatic)
                return false;
            if (GenerationFlags.HasFlag(UserTypeGenerationFlags.UseDirectClassAccess))
                return true;
            return false;
        }

        /// <summary>
        /// Writes IL code for the specified data field as physical property.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="type">The user type containing data field.</param>
        /// <param name="dataField">The data field.</param>
        /// <param name="generatedTypes">The generated types collection.</param>
        private void WritePhysicalDataFieldPropertyCode(ILGenerator il, UserType type, DataFieldUserTypeMember dataField, GeneratedTypes generatedTypes)
        {
            // We shouldn't generate physical data field property code if user type is not physical user type.
            // Also, we should ignore static fields.
            PhysicalUserType physicalType = type as PhysicalUserType;

            if (physicalType == null || dataField.IsStatic || type.IsDeclaredInsideTemplate)
            {
                // This should never happen. It means that IsPhysicalDataFieldProperty doesn't meat the bar for this function.
                throw new NotImplementedException();
            }

            SymbolField field = dataField.Symbol;
            int offset = field.Offset - physicalType.MemoryBufferOffset;
            GeneratedType generatedType = generatedTypes.GetGeneratedType(type);
            Type dataFieldType = generatedTypes.GetType(dataField.Type);

            // Specialization for basic type
            if (dataField.Type is BasicTypeInstance basicType)
            {
                if (basicType.BasicType == typeof(string))
                {
                    int charSize = field.Type.ElementType.Size;

                    // this.ReadString(process, address, charSize, length = -1);
                    il.Emit(OpCodes.Ldarg_0);

                    // this.GetCodeType().Module.Process
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, Defines.Variable_GetCodeType);
                    il.Emit(OpCodes.Callvirt, Defines.CodeType_Module.GetMethod);
                    il.Emit(OpCodes.Callvirt, Defines.Module_Process.GetMethod);

                    // this.ReadPointer(memoryBuffer, memoryBufferOffset + offset, pointerSize)
                    il.Emit(OpCodes.Ldarg_0);

                    // this.memoryBuffer
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBuffer);

                    // this.memoryBufferOffset + offset
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                    EmitConstant(il, offset);
                    il.Emit(OpCodes.Add);

                    // poiterSize
                    EmitConstant(il, field.Type.Size);

                    il.Emit(OpCodes.Call, Defines.UserType_ReadPointer);

                    // charSize
                    EmitConstant(il, charSize);

                    // length = -1
                    EmitConstant(il, -1);

                    il.Emit(OpCodes.Call, Defines.UserType_ReadString);
                    return;
                }

                string basicTypeName = ToUserTypeName(basicType.BasicType);

                if (!string.IsNullOrEmpty(basicTypeName))
                {
                    string readFunctionName = $"Read{basicTypeName}";
                    MethodInfo readFunction;

                    if (field.LocationType == LocationType.BitField)
                        readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length == 4).Single();
                    else
                    {
                        readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length == 2).SingleOrDefault();

                        // Find function that has more than 2 parameters and that starting from third parameter all are default...
                        if (readFunction == null)
                            readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length > 2 && m.GetParameters()[2].HasDefaultValue).Single();
                    }

                    // this.Read{basicTypeName}(memoryBuffer, memoryBufferOffset + offset, [size, bitPosition]);
                    if (!readFunction.IsStatic)
                        il.Emit(OpCodes.Ldarg_0);

                    // this.memoryBuffer
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBuffer);

                    // this.memoryBufferOffset + offset
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                    EmitConstant(il, offset);
                    il.Emit(OpCodes.Add);

                    if (field.LocationType == LocationType.BitField)
                    {
                        // size
                        EmitConstant(il, field.Size);

                        // bitPosition
                        EmitConstant(il, field.BitPosition);

                        // Read function call
                        il.Emit(OpCodes.Call, readFunction);
                    }
                    else
                    {
                        // Read function call with default parameter values
                        ParameterInfo[] parameters = readFunction.GetParameters();

                        for (int i = 2; i < parameters.Length; i++)
                        {
                            ParameterInfo parameter = parameters[i];

                            EmitConstant(il, parameter.DefaultValue);
                        }

                        il.Emit(OpCodes.Call, readFunction);
                    }

                    return;
                }
            }
            // Specialization for arrays
            else if (dataField.Type is ArrayTypeInstance codeArrayType)
            {
                if (codeArrayType.ElementType is BasicTypeInstance basic)
                {
                    string basicTypeName = ToUserTypeName(basic.BasicType);

                    if (!string.IsNullOrEmpty(basicTypeName))
                    {
                        int arraySize = field.Type.Size;
                        int elementSize = field.Type.ElementType.Size;
                        string readFunctionName = $"Read{basicTypeName}Array";
                        MethodInfo readFunction;

                        if (basicTypeName == "Char")
                            readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length == 4).First();
                        else
                            readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length == 3).First();

                        // this.Read{basicTypeName}Array(memoryBuffer, memoryBufferOffset + offset, arraySize / elementSize, [elementSize])";
                        if (!readFunction.IsStatic)
                            il.Emit(OpCodes.Ldarg_0);

                        // this.memoryBuffer
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBuffer);

                        // this.memoryBufferOffset + offset
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                        EmitConstant(il, offset);
                        il.Emit(OpCodes.Add);

                        // arraySize / elementSize
                        EmitConstant(il, arraySize / elementSize);

                        if (basicTypeName == "Char")
                        {
                            // elementSize
                            EmitConstant(il, elementSize);

                            // Read function call
                            il.Emit(OpCodes.Call, readFunction);
                        }
                        else
                        {
                            // Read function call
                            il.Emit(OpCodes.Call, readFunction);
                        }
                    }
                    return;
                }
            }
            // Specialication for enum user type
            else if (dataField.Type is EnumTypeInstance enumType)
            {
                string basicTypeName = ToUserTypeName(enumType.EnumUserType.BasicType);

                if (!string.IsNullOrEmpty(basicTypeName))
                {
                    string readFunctionName = $"Read{basicTypeName}";
                    MethodInfo readFunction;

                    if (field.LocationType == LocationType.BitField)
                        readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length == 4).Single();
                    else
                    {
                        readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length == 2).SingleOrDefault();

                        // Find function that has more than 2 parameters and that starting from third parameter all are default...
                        if (readFunction == null)
                            readFunction = Defines.UserType_Methods.Where(m => m.Name == readFunctionName && m.GetParameters().Length > 2 && m.GetParameters()[2].HasDefaultValue).Single();
                    }

                    // this.Read{basicTypeName}(memoryBuffer, memoryBufferOffset + offset, [size, bitPosition]);
                    if (!readFunction.IsStatic)
                        il.Emit(OpCodes.Ldarg_0);

                    // this.memoryBuffer
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBuffer);

                    // this.memoryBufferOffset + offset
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                    EmitConstant(il, offset);
                    il.Emit(OpCodes.Add);

                    if (field.LocationType == LocationType.BitField)
                    {
                        // size
                        EmitConstant(il, field.Size);

                        // bitPosition
                        EmitConstant(il, field.BitPosition);

                        // Read function call
                        il.Emit(OpCodes.Call, readFunction);
                    }
                    else
                    {
                        // Read function call with default parameter values
                        ParameterInfo[] parameters = readFunction.GetParameters();

                        for (int i = 2; i < parameters.Length; i++)
                        {
                            ParameterInfo parameter = parameters[i];

                            EmitConstant(il, parameter.DefaultValue);
                        }

                        il.Emit(OpCodes.Call, readFunction);
                    }

                    return;
                }
            }
            // Specialization for user types
            else if (dataField.Type is UserTypeInstance userType)
            {
                GeneratedType fieldGeneratedType = generatedTypes.GetGeneratedType(userType.UserType);

                if (field.Type.Tag == Engine.CodeTypeTag.Pointer)
                {
                    // ReadPointer<dataFieldType>(ClassCodeType, dataField.Name, memoryBuffer, memoryBufferOffset + offset, field.Type.Size);

                    // ClassCodeType
                    il.Emit(OpCodes.Ldsfld, generatedType.DefinedFields[ClassCodeTypeFieldName]);

                    // dataField.Name
                    EmitConstant(il, dataField.Name);

                    // memoryBuffer
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBuffer);

                    // memoryBufferOffset + offset
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                    EmitConstant(il, offset);
                    il.Emit(OpCodes.Add);

                    // field.Type.Size
                    EmitConstant(il, field.Type.Size);

                    il.Emit(OpCodes.Call, Defines.UserType_ReadPointer_.MakeGenericMethod(dataFieldType));

                    if (userType.UserType.Symbol.HasVTable() && userType.UserType.DerivedClasses.Count > 0)
                        il.Emit(OpCodes.Call, Defines.VariableCastExtender_DowncastObject_.MakeGenericMethod(dataFieldType));
                    return;
                }
                else if (userType.UserType.Constructors.Contains(UserTypeConstructor.ComplexPhysical))
                {
                    // memoryBuffer
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBuffer);

                    // memoryBufferOffset + offset
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                    EmitConstant(il, offset);
                    il.Emit(OpCodes.Add);

                    // memoryBufferAddress
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferAddress);

                    // codeType
                    if (!userType.UserType.IsDeclaredInsideTemplate && userType.UserType is PhysicalUserType)
                    {
                        // fieldType.ClassCodeType
                        il.Emit(OpCodes.Ldsfld, fieldGeneratedType.DefinedFields[ClassCodeTypeFieldName]);
                    }
                    else
                    {
                        // ClassCodeType.GetClassFieldType(dataField.Name);
                        il.Emit(OpCodes.Ldsfld, generatedType.DefinedFields[ClassCodeTypeFieldName]);
                        EmitConstant(il, dataField.Name);
                        il.Emit(OpCodes.Callvirt, Defines.CodeType_GetClassFieldType);
                    }

                    // memoryBufferAddress + (ulong)(memoryBufferOffset + offset)
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferAddress);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, Defines.UserType_memoryBufferOffset);
                    EmitConstant(il, offset);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Conv_I8);
                    il.Emit(OpCodes.Add);

                    // name
                    EmitConstant(il, dataField.Name);

                    // path
                    EmitConstant(il, Variable.UnknownPath);

                    // Construct new object
                    il.Emit(OpCodes.Newobj, fieldGeneratedType.DefinedConstructors[UserTypeConstructor.ComplexPhysical]);
                    return;
                }
            }
            // Specialization for transformations
            else if (dataField.Type is TransformationTypeInstance transformationType)
            {
                if (field.Type.Tag != Engine.CodeTypeTag.Pointer)
                {
                    // TODO:
                    //string fieldAddress = $"{MemoryBufferAddressFieldName} + (ulong)({MemoryBufferOffsetFieldName} + {offset})";
                    //string fieldVariable = $"{ToString(typeof(Variable))}.CreateNoCast({ClassCodeTypeFieldName}.GetClassFieldType(\"{dataField.Name}\"), {fieldAddress}, \"{dataField.Name}\")";

                    //if (transformationType.Transformation.Transformation.HasPhysicalConstructor)
                    //    fieldVariable = $"{fieldVariable}, {MemoryBufferFieldName}, {MemoryBufferOffsetFieldName} + {offset}, {MemoryBufferAddressFieldName}";
                    //return $"new {dataField.Type.GetTypeString()}({fieldVariable})";
                    throw new NotImplementedException();
                }
            }

            // This should never happen. It means that IsPhysicalDataFieldProperty doesn't meat the bar for this function.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Writes IL code for the specified data field as regular or physical property.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="type">The user type containing data field.</param>
        /// <param name="dataField">The data field.</param>
        /// <param name="generatedTypes">The generated types collection.</param>
        private void WriteDataFieldPropertyCode(ILGenerator il, UserType type, DataFieldUserTypeMember dataField, GeneratedTypes generatedTypes)
        {
            if (IsPhysicalDataFieldProperty(type, dataField))
            {
                WritePhysicalDataFieldPropertyCode(il, type, dataField, generatedTypes);
            }
            else if (GenerationFlags.HasFlag(UserTypeGenerationFlags.UseDirectClassAccess))
            {
                FieldBuilder thisClassField = generatedTypes.GetGeneratedType(type).DefinedFields[ThisClassFieldName];

                // thisClass.Value.GetClassField(dataField.Name);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, thisClassField);
                il.Emit(OpCodes.Call, Defines.UserMember_Variable_Value.GetMethod);
                il.Emit(OpCodes.Ldstr, dataField.Name);
                il.Emit(OpCodes.Callvirt, Defines.Variable_GetClassField);
                CastVariableToDataFieldType(il, dataField, generatedTypes);
            }
            else
            {
                // GetField(dataField.Name);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, dataField.Name);
                il.Emit(OpCodes.Call, Defines.Variable_GetField);
                CastVariableToDataFieldType(il, dataField, generatedTypes);
            }
        }

        /// <summary>
        /// Casts variable on top of the stack to data field type in IL generated code.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="dataField">The data field.</param>
        /// <param name="generatedTypes">The generated types collection.</param>
        private void CastVariableToDataFieldType(ILGenerator il, DataFieldUserTypeMember dataField, GeneratedTypes generatedTypes)
        {
            Type dataFieldType = generatedTypes.GetType(dataField.Type);

            if (dataField.Type is VariableTypeInstance)
            {
                // Do nothing, property code should remain the same
            }
            else if (dataField.Type is EnumTypeInstance enumType)
                CastVariableToBasicType(il, enumType.EnumUserType.BasicType ?? Defines.Int);
            else if (dataField.Type is TemplateTypeInstance templateType && templateType.UserType is EnumUserType enumType2)
                CastVariableToBasicType(il, enumType2.BasicType ?? Defines.Int);
            else if (dataField.Type is BasicTypeInstance basicType)
                CastVariableToBasicType(il, basicType.BasicType);
            else if ((GenerationFlags.HasFlag(UserTypeGenerationFlags.ForceUserTypesToNewInsteadOfCasting)
                || dataField.Type is ArrayTypeInstance || dataField.Type is PointerTypeInstance) && !(dataField.Type is TemplateArgumentTypeInstance))
                il.Emit(OpCodes.Newobj, dataFieldType.GetConstructor(new Type[] { Defines.Variable }));
            else
                il.Emit(OpCodes.Callvirt, Defines.Variable_CastAs_.MakeGenericMethod(dataFieldType));

            // Do downcasting if field is pointer and has vtable
            if (dataField.Symbol.Type.Tag == Engine.CodeTypeTag.Pointer
                && dataField.Type is UserTypeInstance userType)
            {
                if (userType.UserType.Symbol.HasVTable() && userType.UserType.DerivedClasses.Count > 0)
                    il.Emit(OpCodes.Call, Defines.VariableCastExtender_DowncastObject_.MakeGenericMethod(dataFieldType));
            }
        }

        /// <summary>
        /// Casts variable on top of the stack to built-in basic type in IL generated code.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="basicType">The built-in basic type.</param>
        private void CastVariableToBasicType(ILGenerator il, Type basicType)
        {
            if (basicType == typeof(string))
                il.Emit(OpCodes.Callvirt, Defines.Object_ToString);
            else if (basicType == Defines.NakedPointer)
                il.Emit(OpCodes.Newobj, Defines.NakedPointer_Constructor_Variable);
            else if (basicType == typeof(bool))
                il.Emit(OpCodes.Call, Defines.Variable_Bool_Operator);
            else if (basicType == typeof(byte))
                il.Emit(OpCodes.Call, Defines.Variable_Byte_Operator);
            else if (basicType == typeof(sbyte))
                il.Emit(OpCodes.Call, Defines.Variable_SByte_Operator);
            else if (basicType == typeof(char))
                il.Emit(OpCodes.Call, Defines.Variable_Char_Operator);
            else if (basicType == typeof(short))
                il.Emit(OpCodes.Call, Defines.Variable_Short_Operator);
            else if (basicType == typeof(ushort))
                il.Emit(OpCodes.Call, Defines.Variable_UShort_Operator);
            else if (basicType == typeof(int))
                il.Emit(OpCodes.Call, Defines.Variable_Int_Operator);
            else if (basicType == typeof(uint))
                il.Emit(OpCodes.Call, Defines.Variable_UInt_Operator);
            else if (basicType == typeof(long))
                il.Emit(OpCodes.Call, Defines.Variable_Long_Operator);
            else if (basicType == typeof(ulong))
                il.Emit(OpCodes.Call, Defines.Variable_ULong_Operator);
            else if (basicType == typeof(float))
                il.Emit(OpCodes.Call, Defines.Variable_Float_Operator);
            else if (basicType == typeof(double))
                il.Emit(OpCodes.Call, Defines.Variable_Double_Operator);
            else
                throw new NotImplementedException();
        }

        /// <summary>
        /// Extracts constant value out of the specified constant user type member.
        /// If constant is not of built-in type, <c>null</c> will be returned, field should be generated as static readonly
        /// and action for static constructor initialization will be populated.
        /// </summary>
        /// <param name="constant">The constant user type member.</param>
        /// <param name="generatedTypes">The generated types collection.</param>
        /// <param name="staticReadOnlyInitialization">Action for static constructor initialization.</param>
        /// <returns>Constant value or <c>null</c> if action should be used.</returns>
        private object ConstantValue(ConstantUserTypeMember constant, GeneratedTypes generatedTypes, out Action<ILGenerator, FieldBuilder> staticReadOnlyInitialization)
        {
            Type constantType = (constant.Type as BasicTypeInstance)?.BasicType;

            if (constantType != null)
                return ConstantValue(constantType, constant.Value, out staticReadOnlyInitialization);

            if (constant.Type is PointerTypeInstance pointerType)
            {
                ulong cvalue = (ulong)ConvertConstant(typeof(ulong), constant.Value);

                staticReadOnlyInitialization = (il, fieldBuilder) =>
                {
                    EmitConstant(il, cvalue);
                    il.Emit(OpCodes.Newobj, fieldBuilder.FieldType.GetConstructor(new Type[] { typeof(ulong) }));
                    il.Emit(OpCodes.Stsfld, fieldBuilder);
                };
                return null;
            }

            EnumUserType enumUserType = (constant.Type as UserTypeInstance)?.UserType as EnumUserType;

            if (enumUserType != null)
            {
                object basicTypeValue = ConstantValue(enumUserType.BasicType ?? Defines.Int, constant.Value, out staticReadOnlyInitialization);

                return Enum.ToObject(generatedTypes.GetType(constant.Type), basicTypeValue);
            }

            throw new NotImplementedException();
        }

        /// <summary>
        /// Extracts constant value out of the specified type and value.
        /// If constant is not of built-in type, <c>null</c> will be returned, field should be generated as static readonly
        /// and action for static constructor initialization will be populated.
        /// </summary>
        /// <param name="type">The type of the constant.</param>
        /// <param name="value">The constant value.</param>
        /// <param name="staticReadOnlyInitialization">Action for static constructor initialization.</param>
        /// <returns>Constant value or <c>null</c> if action should be used.</returns>
        private object ConstantValue(Type type, object value, out Action<ILGenerator, FieldBuilder> staticReadOnlyInitialization)
        {
            staticReadOnlyInitialization = null;
            if (type == value.GetType())
                return value;

            if (type == typeof(NakedPointer))
            {
                ulong cvalue = (ulong)ConvertConstant(typeof(ulong), value);

                staticReadOnlyInitialization = (il, fieldBuilder) =>
                {
                    EmitConstant(il, cvalue);
                    il.Emit(OpCodes.Newobj, Defines.NakedPointer_Constructor_Ulong);
                    il.Emit(OpCodes.Stsfld, fieldBuilder);
                };
                return null;
            }

            return ConvertConstant(type, value);
        }

        /// <summary>
        /// Converts constant to the specified built-in type.
        /// </summary>
        /// <param name="type">The built-in type.</param>
        /// <param name="value">The constant value.</param>
        /// <returns>Converted constant.</returns>
        private static object ConvertConstant(Type type, object value)
        {
            if (type == value.GetType())
                return value;

            string constantValue = value.ToString();

            if (constantValue[0] == '-')
            {
                if (type == typeof(ulong))
                    return (ulong)long.Parse(constantValue);
                if (type == typeof(uint))
                    return (uint)int.Parse(constantValue);
                if (type == typeof(ushort))
                    return (ushort)short.Parse(constantValue);
                if (type == typeof(byte))
                    return (byte)sbyte.Parse(constantValue);
            }
            if (type == typeof(byte))
                return byte.Parse(constantValue);
            if (type == typeof(sbyte))
                if (sbyte.TryParse(constantValue, out sbyte v))
                    return v;
                else
                    return (sbyte)(byte)(ulong)value;
            if (type == typeof(short))
                return short.Parse(constantValue);
            if (type == typeof(ushort))
                return ushort.Parse(constantValue);
            if (type == typeof(int))
                return int.Parse(constantValue);
            if (type == typeof(uint))
                return uint.Parse(constantValue);
            if (type == typeof(long))
                return long.Parse(constantValue);
            if (type == typeof(ulong))
                return ulong.Parse(constantValue);
            if (type == typeof(float))
                return float.Parse(constantValue);
            if (type == typeof(double))
                return double.Parse(constantValue);
            if (type == typeof(bool))
                return (int.Parse(constantValue) != 0);
            if (type == typeof(char))
                return (char)int.Parse(constantValue);

            throw new NotImplementedException();
        }

        /// <summary>
        /// Emits IL code for the specified constant.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="value">The constant value.</param>
        private static void EmitConstant(ILGenerator il, object value)
        {
            Type type = value.GetType();

            if (type == typeof(bool))
                EmitConstant(il, (bool)value ? 1 : 0);
            else if (type == Defines.Int)
                EmitConstant(il, (int)value);
            else if (type == typeof(uint))
                EmitConstant(il, (uint)value);
            else if (type == typeof(long))
                EmitConstant(il, (long)value);
            else if (type == typeof(ulong))
                EmitConstant(il, (ulong)value);
            else if (type == typeof(string))
                il.Emit(OpCodes.Ldstr, (string)value);
            else
                throw new NotImplementedException();
        }

        /// <summary>
        /// Emits IL code for the specified constant.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="value">The constant value.</param>
        public static void EmitConstant(ILGenerator il, int value)
        {
            if (value == 0)
                il.Emit(OpCodes.Ldc_I4_0);
            else if (value == 1)
                il.Emit(OpCodes.Ldc_I4_1);
            else if (value == 2)
                il.Emit(OpCodes.Ldc_I4_2);
            else if (value == 3)
                il.Emit(OpCodes.Ldc_I4_3);
            else if (value == 4)
                il.Emit(OpCodes.Ldc_I4_4);
            else if (value == 5)
                il.Emit(OpCodes.Ldc_I4_5);
            else if (value == 6)
                il.Emit(OpCodes.Ldc_I4_6);
            else if (value == 7)
                il.Emit(OpCodes.Ldc_I4_7);
            else if (value == 8)
                il.Emit(OpCodes.Ldc_I4_8);
            else if (value == -1)
                il.Emit(OpCodes.Ldc_I4_M1);
            else if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
                il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
            else
                il.Emit(OpCodes.Ldc_I4, value);
        }

        /// <summary>
        /// Emits IL code for the specified constant.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="value">The constant value.</param>
        public static void EmitConstant(ILGenerator il, uint value)
        {
            EmitConstant(il, (int)value);
        }

        /// <summary>
        /// Emits IL code for the specified constant.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="value">The constant value.</param>
        public static void EmitConstant(ILGenerator il, long value)
        {
            if (value >= int.MinValue && value <= int.MaxValue)
            {
                EmitConstant(il, (int)value);
                il.Emit(OpCodes.Conv_I8);
            }
            else
                il.Emit(OpCodes.Ldc_I8, value);
        }

        /// <summary>
        /// Emits IL code for the specified constant.
        /// </summary>
        /// <param name="il">The IL generator.</param>
        /// <param name="value">The constant value.</param>
        public static void EmitConstant(ILGenerator il, ulong value)
        {
            EmitConstant(il, (long)value);
        }

        /// <summary>
        /// Helper class that contains definitions of all used types, methods, constructors and properties for IL generation.
        /// </summary>
        private static class Defines
        {
        #region int
            public static readonly Type Int = typeof(int);
        #endregion

        #region object
            public static readonly Type Object = typeof(object);
            public static readonly MethodInfo Object_ToString = Object.GetMethod("ToString");
        #endregion

        #region string
            public static readonly Type String = typeof(string);
        #endregion

        #region Type
            public static readonly Type Type = typeof(Type);
            public static readonly Type TypeArray = typeof(Type[]);
            public static readonly MethodInfo Type_GetTypeFromHandle = Type.GetMethod("GetTypeFromHandle");
        #endregion

        #region Enum
            public static readonly Type Enum = typeof(Enum);
        #endregion

        #region Flags
            public static readonly Type FlagsAttribute = typeof(System.FlagsAttribute);
            public static readonly ConstructorInfo FlagsAttribute_Constructor = Defines.FlagsAttribute.GetConstructor(new Type[0]);
        #endregion

        #region Func<>
            public static readonly Type Func_ = typeof(Func<>);
            public static readonly Type Func_Variable = typeof(Func<Variable>);
            public static readonly ConstructorInfo Func_Variable_Constructor = Func_Variable.GetConstructor(new Type[] { typeof(object), typeof(IntPtr) });
        #endregion

        #region Variable
            public static readonly Type Variable = typeof(Variable);
            public static readonly MethodInfo[] VariableMethods = Variable.GetMethods();
            public static readonly MethodInfo Variable_GetBaseClass_String = VariableMethods.Where(m => m.Name == "GetBaseClass" && m.ReturnType == Variable && !m.ContainsGenericParameters && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == Defines.String).Single();
            public static readonly MethodInfo Variable_GetBaseClass_Int_This_ = VariableMethods.Where(m => m.Name == "GetBaseClass" && m.ContainsGenericParameters && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == Defines.Int).Single();
            public static readonly MethodInfo Variable_CastAs_ = VariableMethods.Where(m => m.Name == "CastAs" && m.ContainsGenericParameters && m.GetParameters().Length == 0).Single();
            public static readonly MethodInfo Variable_GetClassField = VariableMethods.Where(m => m.Name == "GetClassField" && m.ReturnType == Variable && !m.ContainsGenericParameters && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == Defines.String).Single();
            public static readonly MethodInfo Variable_GetField = VariableMethods.Where(m => m.Name == "GetField" && m.ReturnType == Variable && !m.ContainsGenericParameters && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == Defines.String).Single();
            public static readonly MethodInfo Variable_GetCodeType = VariableMethods.Where(m => m.Name == "GetCodeType").Single();
            public static readonly MethodInfo Variable_GetPointerAddress = VariableMethods.Where(m => m.Name == "GetPointerAddress").Single();
            public static readonly MethodInfo Variable_Bool_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(bool)).Single();
            public static readonly MethodInfo Variable_Byte_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(byte)).Single();
            public static readonly MethodInfo Variable_SByte_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(sbyte)).Single();
            public static readonly MethodInfo Variable_Char_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(char)).Single();
            public static readonly MethodInfo Variable_Short_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(short)).Single();
            public static readonly MethodInfo Variable_UShort_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(ushort)).Single();
            public static readonly MethodInfo Variable_Int_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(int)).Single();
            public static readonly MethodInfo Variable_UInt_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(uint)).Single();
            public static readonly MethodInfo Variable_Long_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(long)).Single();
            public static readonly MethodInfo Variable_ULong_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(ulong)).Single();
            public static readonly MethodInfo Variable_Float_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(float)).Single();
            public static readonly MethodInfo Variable_Double_Operator = VariableMethods.Where(m => m.Name == "op_Explicit" && m.ReturnType == typeof(double)).Single();
        #endregion

        #region CodeType
            public static readonly Type CodeType = typeof(CodeType);
            public static readonly PropertyInfo CodeType_Module = CodeType.GetProperty("Module");
            public static readonly PropertyInfo CodeType_Size = CodeType.GetProperty("Size");
            public static readonly MethodInfo CodeType_GetStaticField = CodeType.GetMethod("GetStaticField");
            public static readonly MethodInfo CodeType_GetClassFieldType = CodeType.GetMethod("GetClassFieldType");
        #endregion

        #region UserType
            public static readonly Type UserType = typeof(CsDebugScript.UserType);
            public static readonly MethodInfo[] UserType_Methods = UserType.GetMethods();
            public static readonly MethodInfo UserType_GetBaseClassString = UserType.GetMethod("GetBaseClassString");
            public static readonly MethodInfo UserType_GetClassCodeType = UserType.GetMethod("GetClassCodeType");
            public static readonly MethodInfo UserType_ReadString = UserType.GetMethod("ReadString");
            public static readonly MethodInfo UserType_ReadPointer = UserType_Methods.Where(m => m.Name == "ReadPointer" && !m.ContainsGenericParameters).Single();
            public static readonly MethodInfo UserType_ReadPointer_ = UserType_Methods.Where(m => m.Name == "ReadPointer" && m.ContainsGenericParameters && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == Defines.CodeType).Single();
            public static readonly FieldInfo UserType_memoryBuffer = UserType.GetField("memoryBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo UserType_memoryBufferAddress = UserType.GetField("memoryBufferAddress", BindingFlags.NonPublic | BindingFlags.Instance);
            public static readonly FieldInfo UserType_memoryBufferOffset = UserType.GetField("memoryBufferOffset", BindingFlags.NonPublic | BindingFlags.Instance);
        #endregion

        #region VariableCastExtender
            public static readonly Type VariableCastExtender = typeof(VariableCastExtender);
            public static readonly MethodInfo VariableCastExtender_DowncastObject_ = VariableCastExtender.GetMethod("DowncastObject");
        #endregion

        #region UserMember
            public static readonly Type UserMember_ = typeof(UserMember<>);
            public static readonly Type UserMember_Variable = UserMember_.MakeGenericType(Defines.Variable);
            public static readonly PropertyInfo UserMember_Variable_Value = UserMember_Variable.GetProperty("Value");
            public static readonly Type UserMember = typeof(UserMember);
            public static readonly MethodInfo[] UserMember_Methods = UserMember.GetMethods();
            public static readonly MethodInfo UserMember_Create = UserMember_Methods.Where(m => m.Name == "Create" && !m.ContainsGenericParameters).Single();
        #endregion

        #region Process
            public static readonly Type Process = typeof(Process);
            public static readonly PropertyInfo Process_Current = Process.GetProperty("Current");
            public static readonly MethodInfo Process_GetGlobal = Process.GetMethod("GetGlobal");
        #endregion

        #region Module
            public static readonly Type Module = typeof(Module);
            public static readonly PropertyInfo Module_Process = Module.GetProperty("Process");
        #endregion

        #region Debugger
            public static readonly Type Debugger = typeof(Debugger);
            public static readonly MethodInfo[] Debugger_Methods = Debugger.GetMethods();
            public static readonly MethodInfo Debugger_ReadMemory = Debugger_Methods.Where(m => m.Name == "ReadMemory" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == Process && m.GetParameters()[1].ParameterType == typeof(ulong) && m.GetParameters()[2].ParameterType == typeof(uint)).Single();
        #endregion

        #region NakedPointer
            public static readonly Type NakedPointer = typeof(NakedPointer);
            public static readonly ConstructorInfo NakedPointer_Constructor_Ulong = NakedPointer.GetConstructor(new Type[] { typeof(ulong) });
            public static readonly ConstructorInfo NakedPointer_Constructor_Variable = NakedPointer.GetConstructor(new Type[] { Defines.Variable });
        #endregion

        #region ICastableObject
            public static readonly Type ICastableObject = typeof(ICastableObject);
        #endregion

        #region TemplateConstantAttribute
            public static readonly Type TemplateConstantAttribute = typeof(TemplateConstantAttribute);
            public static readonly ConstructorInfo TemplateConstantAttribute_Constructor = TemplateConstantAttribute.GetConstructor(new Type[0]);
            public static readonly PropertyInfo[] TemplateConstantAttribute_Properties = new[]
            {
                TemplateConstantAttribute.GetProperty("String"), TemplateConstantAttribute.GetProperty("Value")
            };
        #endregion

        #region DerivedClassAttribute
            public static readonly Type DerivedClassAttribute = typeof(DerivedClassAttribute);
            public static readonly ConstructorInfo DerivedClassAttribute_Constructor = DerivedClassAttribute.GetConstructor(new Type[0]);
            public static readonly PropertyInfo[] DerivedClassAttribute_Properties = new[]
            {
                DerivedClassAttribute.GetProperty("Type"),
                DerivedClassAttribute.GetProperty("Priority"),
                DerivedClassAttribute.GetProperty("TypeName"),
            };
        #endregion

        #region UserTypeAttribute
            public static readonly Type UserTypeAttribute = typeof(UserTypeAttribute);
            public static readonly ConstructorInfo UserTypeAttribute_Constructor = UserTypeAttribute.GetConstructor(new Type[0]);
            public static readonly PropertyInfo[] UserTypeAttribute_Properties = new[]
            {
                UserTypeAttribute.GetProperty("ModuleName"),
                UserTypeAttribute.GetProperty("TypeName"),
            };
        #endregion

        #region BaseClassesArrayAttribute
            public static readonly Type BaseClassesArrayAttribute = typeof(BaseClassesArrayAttribute);
            public static readonly ConstructorInfo BaseClassesArrayAttribute_Constructor = BaseClassesArrayAttribute.GetConstructor(new Type[0]);
            public static readonly PropertyInfo[] BaseClassesArrayAttribute_Properties = new[]
            {
                BaseClassesArrayAttribute.GetProperty("FieldName"),
            };
        #endregion
        }
#endif
    }
}
