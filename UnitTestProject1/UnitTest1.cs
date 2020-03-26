using System;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using ConsoleApp1;
using Moq;
using NUnit.Framework;

namespace UnitTestProject1
{
    [TestFixture]
    public class UnitTest1
    {

        public interface IModel1 : INotifyPropertyChanged
        {
            string Property11 { get; set; }
            string Property12 { get; set; }
        }

        [Test]
        public void GetMethod()
        {
            var stub = new Mock<IModel1>();
            stub.Setup(x => x.Property11).Returns("Property11");
            stub.Setup(x => x.Property12).Returns("Property12");

            var wrapper = BuildFactory<IModel1>().Invoke(stub.Object);

            Assert.That(wrapper.Property11, Is.EqualTo("Property11"));
            Assert.That(wrapper.Property12, Is.EqualTo("Property12"));
        }

        [Test]
        public void SetMethod()
        {
            var stub = new Mock<IModel1>();
            stub.Setup(x => x.Property11).Returns("Property11");
            stub.Setup(x => x.Property12).Returns("Property12");

            stub.VerifyGet(x => x.Property11, Times.Never);
            stub.VerifyGet(x => x.Property12, Times.Never);
            
            var wrapper = BuildFactory<IModel1>().Invoke(stub.Object);
            wrapper.Property11 = "new Property11";
            wrapper.Property12 = "new Property12";

            Assert.That(wrapper.Property11, Is.EqualTo("new Property11"));
            Assert.That(wrapper.Property12, Is.EqualTo("new Property12"));
        }

        private static ModuleBuilder CreateModule()
        {
            AssemblyName aName = new AssemblyName(Guid.NewGuid().ToString());
            AssemblyBuilder ab =
                AppDomain.CurrentDomain.DefineDynamicAssembly(
                    aName,
                    AssemblyBuilderAccess.RunAndSave);
            return ab.DefineDynamicModule(aName.Name, aName.Name + ".dll");
        }

        private static Func<TModel, TModel> BuildFactory<TModel>() 
            where TModel : class, INotifyPropertyChanged
        {
            var type = WrappedModelBase<TModel>.BuildTypeIntoModule(CreateModule());

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