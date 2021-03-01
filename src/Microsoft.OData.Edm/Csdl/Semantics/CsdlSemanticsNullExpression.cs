﻿//---------------------------------------------------------------------
// <copyright file="CsdlSemanticsNullExpression.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using Microsoft.OData.Edm.Csdl.Parsing.Ast;
using Microsoft.OData.Edm.Vocabularies;

namespace Microsoft.OData.Edm.Csdl.CsdlSemantics
{
    /// <summary>
    /// Provides semantics for a Csdl null constant expression.
    /// </summary>
    internal class CsdlSemanticsNullExpression : CsdlSemanticsExpression, IEdmNullExpression
    {
        private readonly CsdlConstantExpression expression;

        public CsdlSemanticsNullExpression(CsdlConstantExpression expression, CsdlSemanticsModel model)
            : base(model, expression)
        {
            this.expression = expression;
        }

        public override CsdlElement Element => this.expression;

        public override EdmExpressionKind ExpressionKind => EdmExpressionKind.Null;

        public EdmValueKind ValueKind => this.expression.ValueKind;

        public IEdmTypeReference Type => null;
    }
}
