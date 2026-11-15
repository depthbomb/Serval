package dev.serval.savanna;

import com.intellij.lexer.Lexer;
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors;
import com.intellij.openapi.editor.colors.TextAttributesKey;
import com.intellij.openapi.fileTypes.SyntaxHighlighter;
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase;
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.vfs.VirtualFile;
import com.intellij.psi.tree.IElementType;
import org.jetbrains.annotations.NotNull;

public final class SavannaHighlighterFactory extends SyntaxHighlighterFactory {
    @Override
    public @NotNull SyntaxHighlighter getSyntaxHighlighter(Project project, VirtualFile file) {
        return new SyntaxHighlighterBase() {
            @Override
            public @NotNull Lexer getHighlightingLexer() {
                return new SavannaLexer();
            }

            @Override
            public TextAttributesKey @NotNull [] getTokenHighlights(IElementType type) {
                if (type == SavannaLexer.COMMENT) {
                    return pack(DefaultLanguageHighlighterColors.BLOCK_COMMENT);
                }

                if (type == SavannaLexer.DIRECTIVE) {
                    return pack(DefaultLanguageHighlighterColors.KEYWORD);
                }

                if (type == SavannaLexer.EXPRESSION) {
                    return pack(DefaultLanguageHighlighterColors.IDENTIFIER);
                }

                return TextAttributesKey.EMPTY_ARRAY;
            }
        };
    }
}
