using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    public interface IModelBase : INotifyPropertyChanged
    {
        string Property { get; set; }
    }

    public interface IModel : IModelBase
    {   
    }

    public abstract class WrappedModelBase<TInterface> : INotifyPropertyChanged
        where TInterface : class, INotifyPropertyChanged
    {
        private delegate void DModifyProperty(ref long mask, int index);
        private sealed class Builder
        {
            private readonly Type _IType = typeof(TInterface);
            private readonly Type _BaseClass = typeof(WrappedModelBase<TInterface>);
            private readonly ConstructorInfo _BaseCtor;

            private readonly MethodInfo _IsModifiedPropertyMI = new Func<long, int, bool>(IsModifiedProperty).Method;
            private readonly MethodInfo _ModifyPropertyMI = new DModifyProperty(ModifyProperty).Method;

            private readonly PropertyInfo _ModelInstanceProperty;

            private readonly IReadOnlyList<PropertyInfo> _IProperties;

            private readonly TypeBuilder _TypeBuilder;
            private readonly IReadOnlyList<FieldInfo> _MasksFields;

            public Builder(ModuleBuilder moduleBuilder)
            {
                _IProperties = GetProperties(_IType);
                _BaseCtor = _BaseClass.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.CreateInstance).First();
                _ModelInstanceProperty = _BaseClass.GetProperty(nameof(ModelInstance), BindingFlags.NonPublic | BindingFlags.Instance);
                _TypeBuilder = moduleBuilder.DefineType($"{_IType.Name}<{Guid.NewGuid().ToString()}>", TypeAttributes.Sealed | TypeAttributes.NotPublic, _BaseClass, new Type[] { _IType });
                _MasksFields = DefineMasksFields();

                DefineProperties();
                DefineCtor();
            }

            public Type CreateType() => _TypeBuilder.CreateType();

            private void DefineCtor()
            {
                var ctor = _TypeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new Type[] { _IType });

                var iLGenerator = ctor.GetILGenerator();
                iLGenerator.Emit(OpCodes.Ldarg_0);
                iLGenerator.Emit(OpCodes.Ldarg_1);
                iLGenerator.Emit(OpCodes.Call, _BaseCtor);
                iLGenerator.Emit(OpCodes.Ret);
            }

            private IReadOnlyList<FieldInfo> DefineMasksFields()
            {
                var masksCount = _IProperties.Count / 64;
                if (masksCount * 64 < _IProperties.Count)
                    masksCount++;

                var suffix = Guid.NewGuid().ToString();
                var result = new FieldInfo[masksCount];
                for (int i = 0; i < result.Length; i++)
                    result[i] = _TypeBuilder.DefineField($"_Mask{i + 1}<{suffix}>", typeof(long), FieldAttributes.Private);
                return result;
            }

            private void DefineProperties()
            {
                const MethodAttributes getSetAttr = MethodAttributes.Public
                    | MethodAttributes.SpecialName
                    | MethodAttributes.HideBySig
                    | MethodAttributes.Virtual;

                var suffix = Guid.NewGuid().ToString();
                for (int i = 0; i < _IProperties.Count; i++)
                {
                    var iPr = _IProperties[i];
                    var prName = $"{iPr.Name}<{suffix}>";
                    var backendField = _TypeBuilder.DefineField($"_{prName}", iPr.PropertyType, FieldAttributes.Private);
                    var pr = _TypeBuilder.DefineProperty(prName, PropertyAttributes.HasDefault, iPr.PropertyType, null);

                    var getter = _TypeBuilder.DefineMethod($"get_{prName}", getSetAttr, iPr.PropertyType, Type.EmptyTypes);
                    buildGetter(getter.GetILGenerator(), iPr, _MasksFields[i / 64], i % 64, backendField);

                    var setter = _TypeBuilder.DefineMethod($"set_{prName}", getSetAttr, null, new Type[] { iPr.PropertyType });
                    var pValue = setter.DefineParameter(0, ParameterAttributes.None, "value");

                    buildSetter(setter.GetILGenerator(), iPr, _MasksFields[i / 64], i % 64, backendField);

                    _TypeBuilder.DefineMethodOverride(getter, iPr.GetMethod);
                    _TypeBuilder.DefineMethodOverride(setter, iPr.SetMethod);
                }


                void buildGetter(ILGenerator iLGenerator, PropertyInfo iPropertyInfo, FieldInfo modfiedPropertiesMask, int bitIndex, FieldInfo backendField)
                {
                    var toBackendLabel = iLGenerator.DefineLabel();

                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldfld, modfiedPropertiesMask);
                    iLGenerator.Emit(OpCodes.Ldc_I4_S, bitIndex);
                    iLGenerator.EmitCall(OpCodes.Call, _IsModifiedPropertyMI, null);
                    iLGenerator.Emit(OpCodes.Brtrue_S, toBackendLabel);

                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.EmitCall(OpCodes.Call, _ModelInstanceProperty.GetMethod, null);
                    iLGenerator.EmitCall(OpCodes.Callvirt, iPropertyInfo.GetMethod, null);
                    iLGenerator.Emit(OpCodes.Ret);

                    iLGenerator.MarkLabel(toBackendLabel);
                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldfld, backendField);
                    iLGenerator.Emit(OpCodes.Ret);
                }

                void buildSetter(ILGenerator iLGenerator, PropertyInfo iPropertyInfo, FieldInfo modfiedPropertiesMask, int bitIndex, FieldInfo backendField)
                {
                    var equalsValuesMI = _BaseClass.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).First(x => x.Name == "EqualsValues");
                    equalsValuesMI = equalsValuesMI.MakeGenericMethod(iPropertyInfo.PropertyType);
                    var onPropertyChangedMI = _BaseClass.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                        .First(x => x.Name == nameof(OnPropertyChanged));

                    var setLabel = iLGenerator.DefineLabel();

                    // if (!WrappedModelBase<IModel>.IsModifiedProperty(_ModifiedProperties, 12) || !WrappedModelBase<IModel>.EqualsValues(_Property, value))
                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldfld, modfiedPropertiesMask);
                    iLGenerator.Emit(OpCodes.Ldc_I4_S, bitIndex);
                    iLGenerator.EmitCall(OpCodes.Call, _IsModifiedPropertyMI, null);
                    iLGenerator.Emit(OpCodes.Brfalse_S, setLabel);

                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldfld, backendField);
                    iLGenerator.Emit(OpCodes.Ldarg_1);
                    iLGenerator.EmitCall(OpCodes.Call, equalsValuesMI, null);
                    iLGenerator.Emit(OpCodes.Brfalse_S, setLabel);

                    iLGenerator.Emit(OpCodes.Ret);

                    // WrappedModelBase<IModel>.ModifyProperty(ref _ModifiedProperties, 12);
                    iLGenerator.MarkLabel(setLabel);
                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldflda, modfiedPropertiesMask);
                    iLGenerator.Emit(OpCodes.Ldc_I4_S, bitIndex);
                    iLGenerator.EmitCall(OpCodes.Call, _ModifyPropertyMI, null);

                    // _Property = value;
                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldarg_1);
                    iLGenerator.Emit(OpCodes.Stfld, backendField);

                    // OnPropertyChanged("Property");
                    iLGenerator.Emit(OpCodes.Ldarg_0);
                    iLGenerator.Emit(OpCodes.Ldstr, iPropertyInfo.Name);
                    iLGenerator.EmitCall(OpCodes.Call, onPropertyChangedMI, null);
                    iLGenerator.Emit(OpCodes.Ret);
                }
            }

            private static IReadOnlyList<PropertyInfo> GetProperties(Type type)
            {
                if (!type.IsInterface)
                    throw new ArgumentException("Supported only interfaces.", nameof(TInterface));

                var result = new List<PropertyInfo>(getProperties(type));
                foreach (var iType in type.GetInterfaces())
                {
                    if (iType == typeof(INotifyPropertyChanged))
                        continue;

                    result.AddRange(getProperties(iType));
                }

                if (result.Count == 0)
                    throw new NotSupportedException("Not supported empty interfaces");

                if (result.GroupBy(x => x.Name).Any(x => x.Skip(1).Any()))
                    throw new NotSupportedException("Property names must be unique");

                return result;

                IReadOnlyList<PropertyInfo> getProperties(Type iType)
                {
                    var properties = iType.GetProperties(BindingFlags.Instance | BindingFlags.Public);

                    var invalidProperties = properties.Where(x => !x.CanRead || !x.CanWrite).ToList();
                    if (invalidProperties.Count > 0)
                    {
                        var message = new StringBuilder("Property must be readdable and writable. Invalid Properties:");
                        foreach (var pr in invalidProperties)
                            message.AppendLine($"\t{pr.DeclaringType.Name}.{pr.Name}");
                        throw new NotSupportedException(message.ToString());
                    }

                    var propMemebers = properties.SelectMany(x => new MemberInfo[] { x, x.GetMethod, x.SetMethod });
                    var members = iType.GetMembers(BindingFlags.Instance | BindingFlags.Public).Except(propMemebers);

                    if (members.Any(x => x.MemberType != MemberTypes.Property))
                        throw new NotSupportedException("Supported only properties");

                    return properties;
                }
            }
        }
        private sealed class WeakSubscription : WeakReference
        {
            private readonly TInterface _ModelInstance;
            private readonly PropertyChangedEventHandler _PropertyChangedEventHandler;

            public WeakSubscription(WrappedModelBase<TInterface> wrapper)
                : base(wrapper)
            {
                if (wrapper == null) throw new ArgumentNullException(nameof(wrapper));

                _ModelInstance = wrapper.ModelInstance;
                _PropertyChangedEventHandler = ModelInstance_PropertyChanged;
            }

            public void Subscribe() => _ModelInstance.PropertyChanged += _PropertyChangedEventHandler;
            public void Unsubscribe() => _ModelInstance.PropertyChanged -= _PropertyChangedEventHandler;

            private bool TryGetTarget(out WrappedModelBase<TInterface> target)
            {
                target = Target as WrappedModelBase<TInterface>;
                if (IsAlive) return true;

                target = null;
                return false;
            }

            private void ModelInstance_PropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (TryGetTarget(out var target))
                    target.RaisePropertyChangedFromModel(e.PropertyName);
                else
                    _ModelInstance.PropertyChanged -= _PropertyChangedEventHandler;
            }
        }

        private WeakSubscription _WeakSubscription;
        protected TInterface ModelInstance { get; }

        private PropertyChangedEventHandler _PropertyChanged;
        public event PropertyChangedEventHandler PropertyChanged
        {
            add
            {
                bool canSubscribe = _PropertyChanged == null;
                _PropertyChanged += value;
                canSubscribe &= _PropertyChanged != null;

                if (canSubscribe)
                {
                    if (_WeakSubscription == null)
                        _WeakSubscription = new WeakSubscription(this);

                    _WeakSubscription.Subscribe();
                }
            }
            remove
            {
                bool canUnsubscribe = _PropertyChanged != null;
                _PropertyChanged -= value;
                canUnsubscribe &= _PropertyChanged == null;

                if (canUnsubscribe)
                {
                    _WeakSubscription.Unsubscribe();
                }
            }
        }

        protected WrappedModelBase(TInterface modelInstance)
        {
            ModelInstance = modelInstance ?? throw new ArgumentNullException(nameof(modelInstance));
        }

        protected bool CanPushChangeFromModel(string propertyName) => true;

        private void RaisePropertyChangedFromModel(string propertyName)
        {
            if (CanPushChangeFromModel(propertyName))
                OnPropertyChanged(propertyName);
        }

        protected void OnPropertyChanged(string propertyName)
            => _PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static bool EqualsValues<TValue>(TValue value, TValue newValue) 
            => EqualityComparer<TValue>.Default.Equals(value, newValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static bool IsModifiedProperty(long mask, int index)
        {
            var propertyMak = 0x1L << index;
            return (mask & propertyMak) == propertyMak;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static void ModifyProperty(ref long mask, int index) 
            => mask |= 0x1L << index;

        public static Type BuildTypeIntoModule(ModuleBuilder moduleBuilder)
        {
            if (moduleBuilder == null) throw new ArgumentNullException(nameof(moduleBuilder));
            var builder = new Builder(moduleBuilder);
            return builder.CreateType();
        }
    }

    public sealed class WrappedModel : WrappedModelBase<IModel>
    {
        private long _ModifiedProperties, _ModifiedProperties1;

        public WrappedModel(IModel model)
            : base(model) { }

        private string _Property;
        public string Property
        {
            get => IsModifiedProperty(_ModifiedProperties, 12) ? _Property : ModelInstance.Property;
            set
            {
                if (IsModifiedProperty(_ModifiedProperties, 12) && EqualsValues(_Property, value))
                    return;

                ModifyProperty(ref _ModifiedProperties, 12);
                _Property = value;
                OnPropertyChanged(nameof(Property));
            }
        }


        //protected override bool CanPushChangeFromModel(string propertyName)
        //{
        //    switch (propertyName)
        //    {
        //        case nameof(Property):
        //            return !IsModifiedProperty(_ModifiedProperties, 0);
        //        case "Property1": return !IsModifiedProperty(_ModifiedProperties, 1);
        //        case "Property2": return !IsModifiedProperty(_ModifiedProperties, 2);
        //        case "Property3": return !IsModifiedProperty(_ModifiedProperties, 3);
        //        case "Property4": return !IsModifiedProperty(_ModifiedProperties, 4);
        //        case "Property5": return !IsModifiedProperty(_ModifiedProperties, 5);
        //        case "Property6": return !IsModifiedProperty(_ModifiedProperties, 6);
        //        case "Property7": return !IsModifiedProperty(_ModifiedProperties, 7);
        //        case "Property8": return !IsModifiedProperty(_ModifiedProperties, 8);
        //        case "Property9": return !IsModifiedProperty(_ModifiedProperties, 9);
        //        case "Property10": return !IsModifiedProperty(_ModifiedProperties1, 10);
        //        case "Property11": return !IsModifiedProperty(_ModifiedProperties1, 11);
        //        case "Property12": return !IsModifiedProperty(_ModifiedProperties1, 12);
        //        case "Property13": return !IsModifiedProperty(_ModifiedProperties1, 13);
        //        case "Property14": return !IsModifiedProperty(_ModifiedProperties1, 14);
        //        case "Property15": return !IsModifiedProperty(_ModifiedProperties1, 15);
        //        case "Property16": return !IsModifiedProperty(_ModifiedProperties1, 16);
        //        case "Property17": return !IsModifiedProperty(_ModifiedProperties1, 17);
        //        case "Property18": return !IsModifiedProperty(_ModifiedProperties1, 18);
        //        case "Property19": return !IsModifiedProperty(_ModifiedProperties1, 19);
        //        case "Property20": return !IsModifiedProperty(_ModifiedProperties1, 20);
        //        default:
        //            return false;
        //    }
        //}
    }

    public sealed class Model : IModel
    {
        public string Property { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    class Program
    {
        static void Main(string[] args)
        {
            AssemblyName aName = new AssemblyName("DynamicAssemblyExample");
            AssemblyBuilder ab =
                AppDomain.CurrentDomain.DefineDynamicAssembly(
                    aName,
                    AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder mb =
                ab.DefineDynamicModule(aName.Name, aName.Name + ".dll");

            var type = WrappedModelBase<IModel>.BuildTypeIntoModule(mb);
            var factory = BuildFactory<IModel>(type);

            var wm = factory(new Model() { Property = "Initial" });
            wm.Property = "Test";
        }

        private static Func<TModel, TModel> BuildFactory<TModel>(Type type)
        {
            var ctor = type.GetConstructor(new[] { typeof(TModel) });
            var parameter = Expression.Parameter(typeof(TModel));
            var lambda = Expression.Lambda<Func<TModel, TModel>>(
                Expression.Convert(
                    Expression.New(ctor, parameter),
                    typeof(TModel)
                 ), parameter);

            return lambda.Compile();
        }
    }
}
