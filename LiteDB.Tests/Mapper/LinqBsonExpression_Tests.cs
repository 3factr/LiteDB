﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB.Tests.Mapper
{
    [TestClass]
    public class LinqBsonExpression_Tests
    {
        #region Model

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public double Salary { get; set; }
            public DateTime CreatedOn { get; set; }
            public bool Active { get; set; }

            public Address Address { get; set; }
            public List<Phone> Phones { get; set; }
            public Phone[] Phones2 { get; set; }

            /// <summary>
            /// This type will be render as new BsonDoctument { [key] = value }
            /// </summary>
            public IDictionary<string, Address> MetaData { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
            public int Number { get; set; }
            public City City { get; set; }

            public string InvalidMethod() => "will thow error in eval";
        }

        public class City
        {
            public string CityName { get; set; }
            public string Country { get; set; }
        }

        public class Phone
        {
            public PhoneType Type { get; set; }
            public int Prefix { get; set; }
            public int Number { get; set; }
        }

        public enum PhoneType
        {
            Mobile, Landline
        }

        private static Address StaticProp { get; set; } = new Address { Number = 99 };
        private const int CONST_INT = 100;
        private string MyMethod() => "ok";
        private int MyIndex() => 5;

        #endregion

        private BsonMapper _mapper = new BsonMapper();

        [TestMethod]
        public void Linq_Document_Navigation()
        {
            // document navigation
            TestExpr<User>(x => x.Id, "_id");
            TestExpr<User>(x => x.Name, "Name");
            TestExpr<User>(x => x.Address.Street, "Address.Street");
            TestExpr<User>(x => x.Address.City.Country, "Address.City.Country");
        }

        [TestMethod]
        public void Linq_Constants()
        {
            // only constants
            var today = DateTime.Today;
            var john = "JOHN";
            var a = new { b = new { c = "JOHN" } };

            // only constants
            TestExpr(x => 0, "@p0", 0);
            TestExpr(x => 1 + 1, "@p0", 2); // "1 + 1" will be resolved by LINQ before convert

            // values from variables
            TestExpr(x => today, "@p0", today);

            // values from deep object variables
            TestExpr(x => a.b.c, "@p0", a.b.c);
            TestExpr<User>(x => x.Address.Street == a.b.c, "(Address.Street = @p0)", a.b.c);

            // class constants
            TestExpr(x => CONST_INT, "@p0", CONST_INT);
            TestExpr(x => StaticProp.Number, "@p0", StaticProp.Number);

            // methods inside constants
            TestExpr(x => "demo".Trim(), "TRIM(@p0)", "demo");

            // execute method inside variables
            TestExpr(x => john.Trim(), "TRIM(@p0)", john);
            TestExpr(x => today.Day, "DAY(@p0)", today);

            // testing node stack using parameter-expression vs variable-expression
            TestExpr<User>(x => x.Name.Length > john.Length, "(LENGTH(Name) > LENGTH(@p0))", john);

            // calling external methods
            TestExpr<User>(x => x.Name == MyMethod(), "(Name = @p0)", MyMethod());
            TestExpr(x => MyMethod().Length, "LENGTH(@p0)", MyMethod());

            TestException<User, NotSupportedException>(() => x => x.Name == x.Address.InvalidMethod());
        }

        [TestMethod]
        public void Linq_Array_Navigation()
        {
            // new `Items` array access
            TestExpr<User>(x => x.Phones.Items().Number, "Phones[*].Number");
            TestExpr<User>(x => x.Phones.Items(1).Number, "Phones[1].Number");
            TestExpr<User>(x => x.Phones.Items(z => z.Prefix == 0).Number, "Phones[(@.Prefix = @p0)].Number", 0);

            // access using native array index (special "get_Item" eval index value)
            TestExpr<User>(x => x.Phones[1].Number, "Phones[1].Number");

            // fixed position
            TestExpr<User>(x => x.Phones[15], "Phones[15]");
            TestExpr<User>(x => x.Phones.ElementAt(1), "Phones[@p0]", 1);

            // fixed position based on method names
            TestExpr<User>(x => x.Phones.First(), "Phones[0]");
            TestExpr<User>(x => x.Phones.Last(), "Phones[-1]");

            // call external method/props/const inside parameter expression
            var a = new { b = new { c = 123 } };

            // Items(int) generate eval value index
            TestExpr<User>(x => x.Phones.Items(a.b.c).Number, "Phones[123].Number");
            TestExpr<User>(x => x.Phones.Items(CONST_INT).Number, "Phones[100].Number");
            TestExpr<User>(x => x.Phones.Items(MyIndex()).Number, "Phones[5].Number");
        }

        [TestMethod]
        public void Linq_Predicate()
        {
            // binary expressions
            TestPredicate<User>(x => x.Active == true, "(Active = @p0)", true);
            TestPredicate<User>(x => x.Salary > 50, "(Salary > @p0)", 50);
            TestPredicate<User>(x => x.Salary != 50, "(Salary != @p0)", 50);
            TestPredicate<User>(x => x.Salary == x.Id, "(Salary = _id)");
            TestPredicate<User>(x => x.Salary > 50 && x.Name == "John", "((Salary > @p0) AND (Name = @p1))", 50, "John");

            // unary expressions
            TestPredicate<User>(x => x.Active, "(Active = true)");
            TestPredicate<User>(x => x.Active && x.Active, "((Active = true) AND (Active = true))");
            TestPredicate<User>(x => x.Active && x.Active && x.Active, "((($.Active=true) AND ($.Active=true)) AND ($.Active=true))");
            TestPredicate<User>(x => x.Active && !x.Active, "((Active = true) AND (Active = false))");
            TestPredicate<User>(x => !x.Active, "(Active = false)");
            TestPredicate<User>(x => !x.Active == true, "((Active = false) = @p0)", true);
            TestPredicate<User>(x => !(x.Salary == 50), "(Salary = @p0) = false", 50);

            // test for precedence order
            TestPredicate<User>(x => x.Name.StartsWith("J") == false, "(Name LIKE (@p0 + '%') = @p1)", "J", false);

            // iif (c ? true : false)
            TestExpr<User>(x => x.Id > 10 ? x.Id : 0, "IIF((_id > @p0), _id, @p1)", 10, 0);
        }

        [TestMethod]
        public void Linq_Cast_Convert_Types()
        {
            // cast only fromType Double/Decimal to Int32/64

            // int cast/convert/parse
            TestExpr<User>(x => (int)x.Salary, "TO_INT32(Salary)");
            TestExpr<User>(x => (int)x.Salary, "TO_INT32(Salary)");
            TestExpr<User>(x => (double)x.Id, "_id");
            TestExpr(x => Convert.ToInt32("123"), "TO_INT32(@p0)", "123");
            TestExpr(x => Int32.Parse("123"), "TO_INT32(@p0)", "123");
        }

        [TestMethod]
        public void Linq_Methods()
        {
            // string instance methods
            TestExpr<User>(x => x.Name.ToUpper(), "UPPER(Name)");
            TestExpr<User>(x => x.Name.Substring(10), "SUBSTRING(Name, @p0)", 10);
            TestExpr<User>(x => x.Name.Substring(10, 20), "SUBSTRING(Name, @p0, @p1)", 10, 20);
            TestExpr<User>(x => x.Name.Replace('+', '-'), "REPLACE(Name, @p0, @p1)", "+", "-");
            TestExpr<User>(x => x.Name.IndexOf("m"), "INDEXOF(Name, @p0)", "m");
            TestExpr<User>(x => x.Name.IndexOf("m", 20), "INDEXOF(Name, @p0, @p1)", "m", 20);
            TestExpr<User>(x => x.Name.ToUpper().Trim(), "TRIM(UPPER(Name))");
            TestExpr<User>(x => x.Name.ToUpper().Trim().PadLeft(5, '0'), "LPAD(TRIM(UPPER($.Name)), @p0, @p1)", 5, "0");

            // string LIKE
            TestExpr<User>(x => x.Name.StartsWith("Mauricio"), "Name LIKE (@p0 + '%')", "Mauricio");
            TestExpr<User>(x => x.Name.Contains("Bezerra"), "Name LIKE ('%' + @p0 + '%')", "Bezerra");
            TestExpr<User>(x => x.Name.EndsWith("David"), "Name LIKE ('%' + @p0)", "David");
            TestExpr<User>(x => x.Name.StartsWith(x.Address.Street), "Name LIKE (Address.Street + '%')");

            // string members
            TestExpr<User>(x => x.Address.Street.Length, "LENGTH(Address.Street)");
            TestExpr(x => string.Empty, "''");

            // string static methods
            TestExpr<User>(x => string.IsNullOrEmpty(x.Name), "(Name = null OR LENGTH(Name) = 0)");
            TestExpr<User>(x => string.IsNullOrWhiteSpace(x.Name), "(Name = null OR LENGTH(TRIM(Name)) = 0)");

            // guid initialize/converter
            TestExpr<User>(x => Guid.NewGuid(), "GUID()");
            TestExpr<User>(x => Guid.Empty, "TO_GUID('00000000-0000-0000-0000-000000000000')");
            TestExpr<User>(x => Guid.Parse("1A3B944E-3632-467B-A53A-206305310BAC"), "TO_GUID(@p0)", "1A3B944E-3632-467B-A53A-206305310BAC");

            // toString/Format
            TestExpr<User>(x => x.Id.ToString(), "TO_STRING(_id)");
            TestExpr<User>(x => x.CreatedOn.ToString(), "TO_STRING(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.ToString("yyyy-MM-dd"), "FORMAT(CreatedOn, @p0)", "yyyy-MM-dd");
            TestExpr<User>(x => x.Salary.ToString("#.##0,00"), "FORMAT(Salary, @p0)", "#.##0,00");

            // adding dates
            TestExpr<User>(x => x.CreatedOn.AddYears(5), "DATEADD('y', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddMonths(5), "DATEADD('M', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddDays(5), "DATEADD('d', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddHours(5), "DATEADD('h', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddMinutes(5), "DATEADD('m', @p0, CreatedOn)", 5);
            TestExpr<User>(x => x.CreatedOn.AddSeconds(5), "DATEADD('s', @p0, CreatedOn)", 5);

            // using dates properties
            TestExpr<User>(x => x.CreatedOn.Year, "YEAR(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Month, "MONTH(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Day, "DAY(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Hour, "HOUR(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Minute, "MINUTE(CreatedOn)");
            TestExpr<User>(x => x.CreatedOn.Second, "SECOND(CreatedOn)");

            TestExpr<User>(x => x.CreatedOn.Date, "TO_DATETIME(YEAR(CreatedOn), MONTH(CreatedOn), DAY(CreatedOn))");

            // static date
            TestExpr<User>(x => DateTime.Now, "NOW()");
            TestExpr<User>(x => DateTime.UtcNow, "NOW_UTC()");
            TestExpr<User>(x => DateTime.Today, "TODAY()");
        }

        [TestMethod]
        public void Linq_Dictionary_Index_Access()
        {
            // index value will be evaluate when "get_Item" method call
            TestExpr<User>(x => x.Phones[0].Number, "Phones[0].Number");
            TestExpr<User>(x => x.MetaData["KeyLocal"], "$.MetaData.KeyLocal");

            TestExpr<User>(x => x.MetaData["Key Local"].Street, "$.MetaData.['Key Local'].Street");
        }

        [TestMethod]
        public void Linq_Sql_Methods()
        {
            // extensions methods aggregation (can change name)
            TestExpr<User>(x => Sql.Sum(x.Phones.Items().Number), "SUM(Phones[*].Number)");
            TestExpr<User>(x => Sql.Max(x.Phones.Items()), "MAX(Phones[*])");
            TestExpr<User>(x => Sql.Count(x.Phones.Items().Number), "COUNT(Phones[*].Number)");
        }

        [TestMethod]
        public void Linq_New_Instance()
        {
            // new class
            TestExpr<User>(x => new { x.Name, x.Address }, "{ Name, Address }");
            TestExpr<User>(x => new { N = x.Name, A = x.Address }, "{ N: $.Name, A: $.Address }");

            // new array
            TestExpr<User>(x => new int[] { x.Id, 6, 7 }, "[_id, @p0, @p1]", 6, 7);

            // new fixed types
            TestExpr(x => new DateTime(2018, 5, 28), "TO_DATETIME(@p0, @p1, @p2)", 2018, 5, 28);

            TestExpr(x => new Guid("1A3B944E-3632-467B-A53A-206305310BAC"), "TO_GUID(@p0)", "1A3B944E-3632-467B-A53A-206305310BAC");

        }

        [TestMethod]
        public void Linq_Complex_Expressions()
        {
            // 'CityName': $.Address.City.CityName, 
            // 'Cnt': COUNT(IIF(TO_STRING($.Phones[@.Type = @p0].Number) = @p1, @p2, $.Name)), 
            // 'List': TO_ARRAY($.Phones[*].Number + $.Phones[@.Prefix > $.Salary].Number)

            TestExpr<User>(x => new
            {
                x.Address.City.CityName,
                Cnt = Sql.Count(x.Phones.Items(z => z.Type == PhoneType.Landline).Number.ToString() == "555" ? MyMethod() : x.Name),
                List = Sql.ToList(x.Phones.Items().Number + x.Phones.Items(z => z.Prefix > x.Salary).Number)
            },
            @"
            {
                CityName: $.Address.City.CityName,
                Cnt: COUNT(IIF((TO_STRING($.Phones[(@.Type = @p0)].Number) = @p1), @p2, $.Name)),
                List: TO_ARRAY(($.Phones[*].Number + $.Phones[(@.Prefix > $.Salary)].Number))    
            }", 
            (int)PhoneType.Landline, "555", MyMethod());
        }

        [TestMethod]
        public void Linq_BsonDocument_Navigation()
        {
            TestExpr<BsonValue>(x => x["name"].AsString, "$.name");
            TestExpr<BsonValue>(x => x["first"]["name"], "$.first.name");
            TestExpr<BsonValue>(x => x["arr"][0]["demo"], "$.arr[0].demo");
            TestExpr<BsonValue>(x => x["phones"].AsArray.Items(z => z["type"] == 1)["number"], "$.phones[(type = @p0)].number", 1);
            TestExpr<BsonValue>(x => x["age"] == 1, "($.age = @p0)", 1);
        }

        #region Test helper

        /// <summary>
        /// Compare LINQ expression with expected BsonExpression
        /// </summary>
        [DebuggerHidden]
        private BsonExpression Test<T, K>(Expression<Func<T, K>> expr, BsonExpression expect, params BsonValue[] args)
        {
            var expression = _mapper.GetExpression(expr);

            Assert.AreEqual(expect.Source, expression.Source);

            Assert.AreEqual(expression.Parameters.Keys.Count, args.Length, "Number of parameter are different than expected");

            var index = 0;

            foreach(var par in args)
            {
                var pval = expression.Parameters["p" + (index++).ToString()];

                Assert.AreEqual(par, pval, "Expression: " + expect.Source);
            }

            return expression;
        }

        [DebuggerHidden]
        private BsonExpression TestExpr(Expression<Func<object, object>> expr, BsonExpression expect, params BsonValue[] args)
        {
            return this.Test<object, object>(expr, expect, args);
        }

        [DebuggerHidden]
        private BsonExpression TestExpr<T>(Expression<Func<T, object>> expr, BsonExpression expect, params BsonValue[] args)
        {
            return this.Test<T, object>(expr, expect, args);
        }

        [DebuggerHidden]
        private BsonExpression TestPredicate<T>(Expression<Func<T, bool>> expr, BsonExpression expect, params BsonValue[] args)
        {
            return this.Test<T, bool>(expr, expect, args);
        }

        /// <summary>
        /// Execute test but expect an exception
        /// </summary>
        [DebuggerHidden]
        private void TestException<T, TExecption>(Func<Expression<Func<T, object>>> fn)
            where TExecption : Exception
        {
            try
            {
                var test = fn();

                this.TestExpr<T>(test, "$");

                Assert.Fail("Should throw exception");
            }
            catch(TExecption)
            {
            }
        }

        #endregion
    }
}