﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal class LinqExpressionVisitor : ExpressionVisitor
    {
        private static Dictionary<Type, ITypeResolver> _resolver = new Dictionary<Type, ITypeResolver>
        {
            [typeof(Convert)] = new ConvertResolver(),
            [typeof(DateTime)] = new DateTimeResolver(),
            [typeof(Decimal)] = new DecimalResolver(),
            [typeof(Double)] = new DoubleResolver(),
            [typeof(Enumerable)] = new EnumerableResolver(),
            [typeof(Guid)] = new GuidResolver(),
            [typeof(Int32)] = new Int32Resolver(),
            [typeof(Int64)] = new Int64Resolver(),
            [typeof(Math)] = new MathResolver(),
            [typeof(ObjectId)] = new ObjectIdResolver(),
            [typeof(Sql)] = new SqlResolver(),
            [typeof(String)] = new StringResolver()
        };

        private readonly BsonMapper _mapper;
        private readonly BsonDocument _parameters = new BsonDocument();

        private string rootParameter = null;
        private int _paramIndex = 0;

        private StringBuilder _builder = new StringBuilder();
        private Stack<Expression> _nodes = new Stack<Expression>();

        public LinqExpressionVisitor(BsonMapper mapper)
        {
            _mapper = mapper;
        }

        public BsonExpression Resolve(Expression expr)
        {
            this.Visit(expr);

            DEBUG(_nodes.Count > 0, "node stack must be empty when finish expression resolve");

            var expression = _builder.ToString();

            try
            {
                return BsonExpression.Create(expression, _parameters);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Invalid BsonExpression when converted from Linq expression: {expr.ToString()} - `{expression}`", ex);
            }
        }

        /// <summary>
        /// Visit :: `x => x.Customer.Name`
        /// </summary>
        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            var l = base.VisitLambda(node);

            // remove last parameter $ (or @)
            _builder.Length--;

            return l;
        }

        /// <summary>
        /// Visit :: x => `x`.Customer.Name
        /// </summary>
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (rootParameter == null) rootParameter = node.Name;

            _builder.Append(node.Name == rootParameter ? "$" : "@");

            return base.VisitParameter(node);
        }

        /// <summary>
        /// Visit :: x => x.`Customer.Name`
        /// </summary>
        protected override Expression VisitMember(MemberExpression node)
        {
            // test if member access is based on parameter expression or constant/external variable
            var isParam = ParameterExpressionVisitor.Test(node);

            var member = node.Member;

            // special types contains method access: string.Length, DateTime.Day, ...
            if (_resolver.TryGetValue(member.DeclaringType, out var type))
            {
                var pattern = type.ResolveMember(member);

                if (pattern == null) throw new NotSupportedException($"Member {member.Name} are not support in {member.DeclaringType.Name} when convert to BsonExpression ({node.ToString()}).");

                this.ResolvePattern(pattern, node.Expression, new Expression[0]);
            }
            else
            {
                // for static member, Expression == null
                if (node.Expression != null)
                {
                    _nodes.Push(node);

                    base.Visit(node.Expression);

                    if (isParam)
                    {
                        var name = this.ResolveMember(member);

                        _builder.Append(name);
                    }
                }
                // static member is not parameter expression - compile and execute as constant
                else
                {
                    var func = Expression.Lambda(node).Compile();
                    var value = func.DynamicInvoke();

                    base.Visit(Expression.Constant(value));
                }
            }

            if (_nodes.Count > 0)
            {
                _nodes.Pop();
            }


            return node;
        }

        /// <summary>
        /// Visit :: x => x.Customer.Name.`ToUpper()`
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // get method declaring type - if is from any kind of list, read as Enumerable
            var declaringType = Reflection.IsList(node.Method.DeclaringType) ? typeof(Enumerable) : node.Method.DeclaringType;

            if (!_resolver.TryGetValue(declaringType, out var type))
            {
                // if method are called by parameter expression and it's not exists, throw error
                var isParam = ParameterExpressionVisitor.Test(node);

                if (isParam) throw new NotSupportedException($"Method {node.Method.Name} not available to convert to BsonExpression ({node.ToString()}).");

                // otherwise, try compile and execute
                var func = Expression.Lambda(node).Compile();
                var value = func.DynamicInvoke();

                base.Visit(Expression.Constant(value));

                return node;
            }

            var pattern = type.ResolveMethod(node.Method);

            if (pattern == null) throw new NotSupportedException($"Method {node.Method.Name} in {node.Method.DeclaringType.Name} are not supported when convert to BsonExpression ({node.ToString()}).");

            // run pattern using object as # and args as @n
            this.ResolvePattern(pattern, node.Object, node.Arguments);

            return node;
        }

        /// <summary>
        /// Visit :: x => x.Age + `10` (will create parameter:  `p0`, `p1`, ...)
        /// </summary>
        protected override Expression VisitConstant(ConstantExpression node)
        {
            MemberExpression prevNode;
            var value = node.Value;

            // https://stackoverflow.com/a/29708655/3286260
            while (_nodes.Count > 0 && (prevNode = _nodes.Peek() as MemberExpression) != null)
            {
                if (prevNode.Member is FieldInfo fieldInfo)
                {
                    value = fieldInfo.GetValue(value);
                }
                else if (prevNode.Member is PropertyInfo propertyInfo)
                {
                    value = propertyInfo.GetValue(value);
                }

                _nodes.Pop();
            }

            DEBUG(_nodes.Count > 0, "counter stack must be zero to eval all properties/field over object");

            var parameter = "p" + (_paramIndex++);

            _builder.AppendFormat("@" + parameter);

            var arg = value == null ? BsonValue.Null : _mapper.Serialize(value.GetType(), value);

            _parameters[parameter] = arg;

            return node;
        }

        /// <summary>
        /// Visit :: x => `!x.Active`
        /// </summary>
        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Not)
            {
                _builder.Append("(");
                this.Visit(node.Operand);
                _builder.Append(") = false");
            }
            else
            {
                base.VisitUnary(node);
            }

            return node;
        }

        /// <summary>
        /// Visit :: x => `new { x.Id, x.Name }`
        /// </summary>
        protected override Expression VisitNew(NewExpression node)
        {
            if (node.Members == null)
            {
                if (_resolver.TryGetValue(node.Type, out var type))
                {
                    var pattern = type.ResolveCtor(node.Constructor);

                    if (pattern == null) throw new NotSupportedException($"Constructor for {node.Type.Name} are not supported when convert to BsonExpression ({node.ToString()}).");

                    this.ResolvePattern(pattern, null, node.Arguments);
                }
                else
                {
                    throw new NotSupportedException($"New instance are not supported for {node.Type} when convert to BsonExpression ({node.ToString()}).");
                }
            }
            else
            {
                _builder.Append("{ ");

                for (var i = 0; i < node.Members.Count; i++)
                {
                    var member = node.Members[i];
                    _builder.Append(i > 0 ? ", " : "");
                    _builder.AppendFormat("'{0}': ", member.Name);
                    this.Visit(node.Arguments[i]);
                }

                _builder.Append(" }");
            }

            return node;
        }

        /// <summary>
        /// Visit :: x => `new int[] { 1, 2, 3 }`
        /// </summary>
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            _builder.Append("[ ");

            for (var i = 0; i < node.Expressions.Count; i++)
            {
                _builder.Append(i > 0 ? ", " : "");
                this.Visit(node.Expressions[i]);
            }

            _builder.Append(" ]");

            return node;
        }

        /// <summary>
        /// Visit :: x => x.Id `+` 10
        /// </summary>
        protected override Expression VisitBinary(BinaryExpression node)
        {
            var op = this.GetOperator(node.NodeType);

            base.Visit(node.Left);

            _builder.Append(op);

            base.Visit(node.Right);

            // when object is native array, access child using [n] is an Binary expression
            if (op == "[") _builder.Append("]");

            return node;
        }

        /// <summary>
        /// Visit :: x => `x.Id > 0 ? "ok" : "not-ok"`
        /// </summary>
        protected override Expression VisitConditional(ConditionalExpression node)
        {
            _builder.Append("IIF(");
            this.Visit(node.Test);
            _builder.Append(", ");
            this.Visit(node.IfTrue);
            _builder.Append(", ");
            this.Visit(node.IfFalse);
            _builder.Append(")");

            return node;
        }

        /// <summary>
        /// Resolve string pattern using an object + N arguments. Will write over _builder
        /// </summary>
        private void ResolvePattern(string pattern, Expression obj, IEnumerable<Expression> args)
        {
            // retain current builder/stack/isMemberParameter variables
            var currentBuilder = _builder;
            var currentNodes = _nodes;

            // create new instances
            _builder = new StringBuilder();
            _nodes = new Stack<Expression>();

            if (obj != null)
            {
                this.Visit(obj);
            }

            // get object expression string
            var objectExpr = _builder.ToString();
            var parameters = new Dictionary<int, string>();
            var index = 0;

            // now, get all parameter expressions strings
            foreach (var arg in args)
            {
                _builder = new StringBuilder();
                this.Visit(arg);
                parameters[index++] = _builder.ToString();
            }

            // now, do replace for # to objet and @N as parameters
            var output = new StringBuilder();
            var tokenizer = new Tokenizer(pattern);

            // lets use tokenizer to parse this method pattern
            while (!tokenizer.EOF)
            {
                var token = tokenizer.ReadToken(false);

                if (token.Type == TokenType.Hashtag)
                {
                    output.Append(objectExpr);
                }
                else if (token.Type == TokenType.At)
                {
                    var i = Convert.ToInt32(tokenizer.ReadToken(false).Expect(TokenType.Int).Value);
                    output.Append(parameters[i]);
                }
                else
                {
                    output.Append(token.Type == TokenType.String ? "'" + token.Value + "'" : token.Value);
                }
            }

            // now restore instances before run pattern
            _builder = currentBuilder;
            _nodes = currentNodes;

            // append into builder result of resolve pattern
            _builder.Append(output.ToString());
        }

        /// <summary>
        /// Get string operator from an Binary expression
        /// </summary>
        private string GetOperator(ExpressionType nodeType)
        {
            switch (nodeType)
            {
                case ExpressionType.Add: return " + ";
                case ExpressionType.Multiply: return " * ";
                case ExpressionType.Subtract: return " - ";
                case ExpressionType.Divide: return " / ";
                case ExpressionType.Equal: return " = ";
                case ExpressionType.NotEqual: return " != ";
                case ExpressionType.GreaterThan: return " > ";
                case ExpressionType.GreaterThanOrEqual: return " >= ";
                case ExpressionType.LessThan: return " < ";
                case ExpressionType.LessThanOrEqual: return " <= ";
                case ExpressionType.AndAlso: return " AND ";
                case ExpressionType.OrElse: return " OR ";
                case ExpressionType.ArrayIndex: return "[";
            }

            throw new NotSupportedException("Operator not supported: " + nodeType.ToString());
        }

        /// <summary>
        /// Returns document field name for some type member
        /// </summary>
        private string ResolveMember(MemberInfo member)
        {
            var name = member.Name;

            // get class entity from mapper
            var entity = _mapper.GetEntityMapper(member.DeclaringType);

            var field = entity.Members.FirstOrDefault(x => x.MemberName == name);

            if (field == null) throw new NotSupportedException($"Member {name} not found on BsonMapper for type {member.DeclaringType}.");

            return "." + field.FieldName;
        }
    }
}