//---------------------------------------------------------------------
// <copyright file="CsdlSemanticsStringConstantExpression.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using System;
using Microsoft.OData.Edm.Csdl.Parsing.Ast;
using Microsoft.OData.Edm.Vocabularies;

namespace Microsoft.OData.Edm.Csdl.CsdlSemantics
{
    /// <summary>
    /// Provides semantics for a Csdl String constant expression.
    /// </summary>
    internal class CsdlSemanticsStringConstantExpression : CsdlSemanticsExpression, IEdmStringConstantExpression
    {
        private readonly CsdlConstantExpression expression;

        private readonly Cache<CsdlSemanticsStringConstantExpression, string> valueCache = new Cache<CsdlSemanticsStringConstantExpression, string>();
        private static readonly Func<CsdlSemanticsStringConstantExpression, string> ComputeValueFunc = (me) => me.ComputeValue();

        public CsdlSemanticsStringConstantExpression(CsdlConstantExpression expression, CsdlSemanticsModel model)
            : base(model, expression)
        {
            this.expression = expression;
        }

        public override CsdlElement Element => this.expression;

        public string Value
        {
            get { return this.valueCache.GetValue(this, ComputeValueFunc, null); }
        }

        public override EdmExpressionKind ExpressionKind => EdmExpressionKind.StringConstant;

        public IEdmTypeReference Type => null;

        public EdmValueKind ValueKind => this.expression.ValueKind;

        private string ComputeValue()
        {
            // [EdmLib] Determine what escaping is necessary in String expression, or do not cache the value.
            return this.expression.Value;
        }
    }
}
