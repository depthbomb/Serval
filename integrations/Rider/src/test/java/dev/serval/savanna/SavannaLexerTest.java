package dev.serval.savanna;

import java.util.ArrayList;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public final class SavannaLexerTest {
    @Test
    public void stringsAndCommentsDoNotEndAnExpression() {
        String source = "<p>{{ Model.Call(\"}}\", @\"a\"\"}}\", \"\"\"raw }}\"\"\") /* }} */ }}</p>";
        assertEquals(List.of("TEXT", "EXPRESSION", "TEXT"), tokens(source));
    }

    @Test
    public void commentsAndVerbatimDoNotParseNestedTemplates() {
        assertEquals(List.of("COMMENT", "DIRECTIVE", "TEXT", "DIRECTIVE", "EXPRESSION"),
            tokens("{# {{ hidden }} #}{% verbatim %}{{ literal }}{% endverbatim %}{{ Model.Title }}"));
    }

    @Test
    public void lexerCanRestartAtEveryTokenBoundary() {
        String source = "{% model string %}<p>{% verbatim %}{{ literal }}{% endverbatim %}{{ Model }}</p>";
        SavannaLexer lexer = new SavannaLexer();
        lexer.start(source);
        while (lexer.getTokenType() != null) {
            SavannaLexer restarted = new SavannaLexer();
            restarted.start(source, lexer.getTokenStart(), source.length(), lexer.getState());
            assertEquals(lexer.getTokenType(), restarted.getTokenType());
            assertEquals(lexer.getTokenEnd(), restarted.getTokenEnd());
            lexer.advance();
        }
    }

    @Test
    public void unfinishedExpressionsStillCoverEveryCharacter() {
        assertEquals(List.of("TEXT", "EXPRESSION"), tokens("<p>{{ Model."));
    }

    @Test
    public void nestedInterpolatedStringsDoNotEndAnExpression() {
        for (String expression : List.of(
                "$\"{\"}}\"}\"", "$@\"{\"}}\"}\"", "@$\"{\"}}\"}\"",
                "$\"{Model.Call($\"{Model.Call(\"}}\")}\")}\"",
                "$$\"\"\"{{\"}}\"}}\"\"\"")) {
            String source = "{{ " + expression + " }}<p>after</p>";
            var lexer = new SavannaLexer();
            lexer.start(source);
            assertEquals(SavannaLexer.EXPRESSION, lexer.getTokenType());
            assertEquals(source.indexOf("<p>"), lexer.getTokenEnd());
            assertEquals(List.of("EXPRESSION", "TEXT"), tokens(source));
        }
    }

    @Test
    public void verbatimDirectivesAllowWhitespaceLikeTheCompiler() {
        assertEquals(List.of("DIRECTIVE", "TEXT", "DIRECTIVE", "EXPRESSION"),
            tokens("{%\tverbatim\r\n%}{{ literal }}{% endverbatim %}{{ Model }}"));
    }

    @Test
    public void carriageReturnEndsCSharpLineComments() {
        assertEquals(List.of("EXPRESSION", "TEXT"), tokens("{{ Model // comment\r }}<p>after</p>"));
    }

    private static List<String> tokens(String source) {
        var result = new ArrayList<String>();
        var lexer = new SavannaLexer();
        lexer.start(source);
        int end = 0;
        while (lexer.getTokenType() != null) {
            assertEquals(end, lexer.getTokenStart());
            assertTrue(lexer.getTokenEnd() > lexer.getTokenStart());
            result.add(lexer.getTokenType().toString());
            end = lexer.getTokenEnd();
            lexer.advance();
        }
        assertEquals(source.length(), end);
        return result;
    }
}
