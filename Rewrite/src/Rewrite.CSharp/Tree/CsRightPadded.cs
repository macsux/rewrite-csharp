namespace Rewrite.RewriteCSharp.Tree;

public interface CsRightPadded
{
    public record Location(CsSpace.Location AfterLocation)
    {
        public static readonly Location ARRAY_RANK_SPECIFIER_SIZE = new(CsSpace.Location.ARRAY_RANK_SPECIFIER_SIZE_SUFFIX);
        public static readonly Location ATTRIBUTE_LIST_ATTRIBUTES = new(CsSpace.Location.ATTRIBUTE_LIST_ATTRIBUTE_SUFFIX);
        public static readonly Location ATTRIBUTE_LIST_TARGET = new(CsSpace.Location.ATTRIBUTE_LIST_TARGET_SUFFIX);
        public static readonly Location BLOCK_SCOPE_NAMESPACE_DECLARATION_MEMBERS = new(CsSpace.Location.BLOCK_SCOPE_NAMESPACE_DECLARATION_MEMBERS);
        public static readonly Location BLOCK_SCOPE_NAMESPACE_DECLARATION_NAME = new(CsSpace.Location.BLOCK_SCOPE_NAMESPACE_DECLARATION_NAME);
        public static readonly Location BLOCK_SCOPE_NAMESPACE_DECLARATION_USINGS = new(CsSpace.Location.BLOCK_SCOPE_NAMESPACE_DECLARATION_USINGS);
        public static readonly Location COLLECTION_EXPRESSION_ELEMENTS = new(CsSpace.Location.COLLECTION_EXPRESSION_ELEMENTS);
        public static readonly Location COMPILATION_UNIT_MEMBERS = new(CsSpace.Location.COMPILATION_UNIT_MEMBERS);
        public static readonly Location COMPILATION_UNIT_USINGS = new(CsSpace.Location.COMPILATION_UNIT_USINGS);
        public static readonly Location FILE_SCOPE_NAMESPACE_DECLARATION_MEMBERS = new(CsSpace.Location.FILE_SCOPE_NAMESPACE_DECLARATION_MEMBERS);
        public static readonly Location FILE_SCOPE_NAMESPACE_DECLARATION_NAME = new(CsSpace.Location.FILE_SCOPE_NAMESPACE_DECLARATION_NAME);
        public static readonly Location FILE_SCOPE_NAMESPACE_DECLARATION_USINGS = new(CsSpace.Location.FILE_SCOPE_NAMESPACE_DECLARATION_USINGS);
        public static readonly Location NULL_SAFE_EXPRESSION_EXPRESSION = new(CsSpace.Location.NULL_SAFE_EXPRESSION_EXPRESSION_SUFFIX);
        public static readonly Location USING_DIRECTIVE_ALIAS = new(CsSpace.Location.USING_DIRECTIVE_ALIAS);
        public static readonly Location USING_DIRECTIVE_GLOBAL = new(CsSpace.Location.USING_DIRECTIVE_GLOBAL_SUFFIX);
    }
}