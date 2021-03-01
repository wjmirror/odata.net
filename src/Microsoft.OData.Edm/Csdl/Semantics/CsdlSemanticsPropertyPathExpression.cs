﻿//---------------------------------------------------------------------
// <copyright file="CsdlSemanticsPropertyPathExpression.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using Microsoft.OData.Edm.Csdl.Parsing.Ast;

namespace Microsoft.OData.Edm.Csdl.CsdlSemantics
{
    /// <summary>
    /// Provides semantics for a Csdl Property Path expression.
    /// </summary>
    internal class CsdlSemanticsPropertyPathExpression : CsdlSemanticsPathExpression
    {
        public CsdlSemanticsPropertyPathExpression(CsdlPathExpression expression, IEdmEntityType bindingContext, CsdlSemanticsModel model)
            : base(expression, bindingContext, model)
        {
        }

        public override EdmExpressionKind ExpressionKind => EdmExpressionKind.PropertyPath;
    }
}
