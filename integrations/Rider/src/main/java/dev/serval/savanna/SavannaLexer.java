package dev.serval.savanna;

import com.intellij.lexer.LexerBase;
import com.intellij.psi.tree.IElementType;
import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

public final class SavannaLexer extends LexerBase {
    public static final IElementType TEXT = new IElementType("TEXT", SavannaLanguage.INSTANCE);
    public static final IElementType DIRECTIVE = new IElementType("DIRECTIVE", SavannaLanguage.INSTANCE);
    public static final IElementType EXPRESSION = new IElementType("EXPRESSION", SavannaLanguage.INSTANCE);
    public static final IElementType COMMENT = new IElementType("COMMENT", SavannaLanguage.INSTANCE);
    private CharSequence buffer = "";
    private int end;
    private int start;
    private int position;
    private int state;
    private int nextState;
    private IElementType type;

    @Override
    public void start(@NotNull CharSequence buffer, int startOffset, int endOffset, int initialState) {
        this.buffer = buffer;
        this.end = endOffset;
        this.position = startOffset;
        this.nextState = initialState;
        advance();
    }

    @Override
    public int getState() {
        return state;
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
        state = nextState;
        if (start >= end) {
            type = null;
            return;
        }

        if (state == 1 || state == 2) {
            int closing = indexOf(state == 1 ? "{% endverbatim %}" : "</#verbatim>", start);
            if (closing != start) {
                position = closing < 0 ? end : closing;
                type = TEXT;
                return;
            }

            nextState = 0;
        }

        if (matches(start, "<#--")) {
            int closing = indexOf("-->", start + 4);
            position = closing < 0 ? end : closing + 3;
            type = COMMENT;
            return;
        }

        if (matches(start, "<#") || matches(start, "</#")) {
            type = DIRECTIVE;
            position = tagEnd(start + 2);
            if (matches(start, "<#verbatim") && buffer.subSequence(start + "<#verbatim".length(), position).toString().trim().equals(">")) {
                nextState = 2;
            }
            return;
        }

        if (matches(start, "{#")) {
            int closing = indexOf("#}", start + 2);
            position = closing < 0 ? end : closing + 2;
            type = COMMENT;
            return;
        }

        if (matches(start, "{{") || matches(start, "{%")) {
            type = buffer.charAt(start + 1) == '{' ? EXPRESSION : DIRECTIVE;
            position = expressionEnd(start + 2, type == EXPRESSION ? "}}" : "%}");
            if (type == DIRECTIVE && position >= start + 4 && matches(position - 2, "%}")
                    && buffer.subSequence(start + 2, position - 2).toString().strip().equals("verbatim")) {
                nextState = 1;
            }
            return;
        }

        type = TEXT;
        position++;
        while (position < end && !matches(position, "{{") && !matches(position, "{%") && !matches(position, "{#")
                && !matches(position, "<#") && !matches(position, "</#")) {
            position++;
        }
    }

    private int tagEnd(int offset) {
        char quote = 0;
        for (int cursor = offset; cursor < end; cursor++) {
            char character = buffer.charAt(cursor);
            if (quote != 0) {
                if (character == quote) {
                    quote = 0;
                }
            } else if (character == '\'' || character == '"') {
                quote = character;
            } else if (character == '>') {
                return cursor + 1;
            }
        }
        return end;
    }

    private int expressionEnd(int offset, String delimiter) {
        return expressionEnd(offset, delimiter, 0);
    }

    private int expressionEnd(int offset, String delimiter, int nesting) {
        // Incomplete editor input can contain arbitrarily nested string interpolations.
        if (nesting >= 128) {
            return end;
        }

        int depth = 0;
        int cursor = offset;
        while (cursor < end) {
            if (depth == 0 && matches(cursor, delimiter)) {
                return cursor + delimiter.length();
            }

            char c = buffer.charAt(cursor);
            if (c == '"' || c == '\'') {
                cursor = stringEnd(cursor, nesting);
                continue;
            }

            if (matches(cursor, "//")) {
                cursor += 2;
                while (cursor < end && !isNewline(buffer.charAt(cursor))) {
                    cursor++;
                }
                continue;
            }

            if (matches(cursor, "/*")) {
                int closing = indexOf("*/", cursor + 2);
                cursor = closing < 0 ? end : closing + 2;
                continue;
            }

            if (c == '(' || c == '[' || c == '{') {
                depth++;
            } else if (c == ')' || c == ']' || c == '}') {
                depth = Math.max(0, depth - 1);
            }
            cursor++;
        }

        return end;
    }

    private int stringEnd(int offset, int nesting) {
        char quote = buffer.charAt(offset);
        int prefix = offset;
        boolean verbatim = quote == '"' && prefix > 0 && buffer.charAt(prefix - 1) == '@';
        if (verbatim) {
            prefix--;
        }
        int dollars = 0;
        while (quote == '"' && prefix > 0 && buffer.charAt(prefix - 1) == '$') {
            dollars++;
            prefix--;
        }
        verbatim |= quote == '"' && dollars > 0 && prefix > 0 && buffer.charAt(prefix - 1) == '@';
        int quotes = 1;
        while (quote == '"' && !verbatim && offset + quotes < end && buffer.charAt(offset + quotes) == quote) {
            quotes++;
        }
        boolean raw = quotes >= 3;
        String closing = raw ? "\"".repeat(quotes) : String.valueOf(quote);
        String openingBrace = "{".repeat(raw ? dollars : 1);
        String closingBrace = "}".repeat(raw ? dollars : 1);
        int cursor = offset + (raw ? quotes : 1);
        while (cursor < end) {
            if (!raw && !verbatim && buffer.charAt(cursor) == '\\') {
                cursor = Math.min(end, cursor + 2);
                continue;
            }
            if (matches(cursor, closing)) {
                cursor += closing.length();
                if (verbatim && cursor < end && buffer.charAt(cursor) == quote) {
                    cursor++;
                    continue;
                }
                return cursor;
            }
            if (dollars > 0 && matches(cursor, openingBrace)) {
                if (!raw && matches(cursor, "{{")) {
                    cursor += 2;
                    continue;
                }
                cursor = expressionEnd(cursor + openingBrace.length(), closingBrace, nesting + 1);
                continue;
            }
            cursor++;
        }

        return end;
    }

    private static boolean isNewline(char value) {
        return value == '\r' || value == '\n' || value == '\u0085' || value == '\u2028' || value == '\u2029';
    }

    private boolean matches(int offset, String value) {
        if (offset + value.length() > end) {
            return false;
        }

        for (int index = 0; index < value.length(); index++) {
            if (buffer.charAt(offset + index) != value.charAt(index)) {
                return false;
            }
        }

        return true;
    }

    private int indexOf(String value, int offset) {
        for (int index = offset; index + value.length() <= end; index++) {
            if (matches(index, value)) {
                return index;
            }
        }

        return -1;
    }
}
