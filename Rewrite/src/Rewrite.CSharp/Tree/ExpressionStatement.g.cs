//------------------------------------------------------------------------------
// <auto-generated>
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
#nullable enable
#pragma warning disable CS0108
using System.Diagnostics.CodeAnalysis;
using Rewrite.Core;
using Rewrite.Core.Marker;
using FileAttributes = Rewrite.Core.FileAttributes;
using Rewrite.RewriteJava.Tree;

namespace Rewrite.RewriteCSharp.Tree;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "PossibleUnintendedReferenceComparison")]
[SuppressMessage("ReSharper", "InvertIf")]
[SuppressMessage("ReSharper", "RedundantExtendsListEntry")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "RedundantNameQualifier")]
public partial interface Cs : J
{
    public partial class ExpressionStatement(
    Guid id,
    Space prefix,
    Markers markers,
    Expression expression
    ) : Cs, Statement, MutableTree<ExpressionStatement>
    {
        public J? AcceptCSharp<P>(CSharpVisitor<P> v, P p)
        {
            return v.VisitExpressionStatement(this, p);
        }

        public Guid Id => id;

        public ExpressionStatement WithId(Guid newId)
        {
            return newId == id ? this : new ExpressionStatement(newId, prefix, markers, expression);
        }
        public Space Prefix => prefix;

        public ExpressionStatement WithPrefix(Space newPrefix)
        {
            return newPrefix == prefix ? this : new ExpressionStatement(id, newPrefix, markers, expression);
        }
        public Markers Markers => markers;

        public ExpressionStatement WithMarkers(Markers newMarkers)
        {
            return ReferenceEquals(newMarkers, markers) ? this : new ExpressionStatement(id, prefix, newMarkers, expression);
        }
        public Expression Expression => expression;

        public ExpressionStatement WithExpression(Expression newExpression)
        {
            return ReferenceEquals(newExpression, expression) ? this : new ExpressionStatement(id, prefix, markers, newExpression);
        }
        public bool Equals(Rewrite.Core.Tree? other)
        {
            return other is ExpressionStatement && other.Id == Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}