package dev.serval.savanna;

import com.intellij.lexer.LexerBase;
import com.intellij.psi.TokenType;
import com.intellij.psi.tree.IElementType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

// PSI needs word-sized leaves for Ctrl-hover. The editor's separate lexer keeps HTML layers intact.
public final class SavannaPsiLexer extends LexerBase {
    public static final IElementType WORD = new IElementType("WORD", SavannaLanguage.INSTANCE);
    public static final IElementType SYMBOL = new IElementType("SYMBOL", SavannaLanguage.INSTANCE);
    private CharSequence buffer = "";
    private int end;
    private int start;
    private int position;
    private IElementType type;

    @Override
    public void start(@NotNull CharSequence buffer, int startOffset, int endOffset, int initialState) {
        this.buffer = buffer;
        this.end = endOffset;
        this.position = startOffset;
        advance();
    }

    @Override
    public int getState() {
        return 0;
    }

    @Override
    public @Nullable IElementType getTokenType() {
        return type;
    }

    @Override
    public int getTokenStart() {
        return start;
    }

    @Override
    public int getTokenEnd() {
        return position;
    }

    @Override
    public @NotNull CharSequence getBufferSequence() {
        return buffer;
    }

    @Override
    public int getBufferEnd() {
        return end;
    }

    @Override
    public void advance() {
        start = position;
        if (start >= end) {
            type = null;
            return;
        }

        int point = Character.codePointAt(buffer, position);
        type = Character.isWhitespace(point) ? TokenType.WHITE_SPACE : isWord(point) ? WORD : SYMBOL;
        position = Math.min(end, position + Character.charCount(point));
        while (position < end && type != SYMBOL) {
            point = Character.codePointAt(buffer, position);
            if (type == WORD ? !isWord(point) : !Character.isWhitespace(point)) {
                break;
            }
            position = Math.min(end, position + Character.charCount(point));
        }
    }

    private static boolean isWord(int point) {
        return point == '@' || Character.isUnicodeIdentifierPart(point);
    }
}

