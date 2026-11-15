package dev.serval.savanna;

import com.intellij.testFramework.ApplicationRule;
import com.intellij.psi.PsiFile;
import java.lang.reflect.Proxy;
import java.net.URI;
import java.nio.file.Files;
import org.junit.ClassRule;
import org.junit.Test;
import static org.junit.Assert.*;

public final class SavannaNavigationTest {
    @ClassRule
    public static final ApplicationRule APPLICATION = new ApplicationRule();

    @Test
    public void testDefinitionTargetsUseWindowsFileUrisAndRefreshNewMetadata() throws Exception {
        var directory = Files.createTempDirectory("savanna-navigation");
        var target = directory.resolve("Class With Space.cs");
        Files.writeString(target, "public class Example {}");
        var resolved = SavannaLspIntegration.findLocalFile(target.toUri());
        assertNotNull("The definition URI must resolve to a Rider virtual file", resolved);
        assertTrue("The resolved URI must identify the requested file", Files.isSameFile(target, resolved.toNioPath()));
        assertEquals(resolved, SavannaLspIntegration.findLocalFile(URI.create(target.toUri().toString().replace("C:", "c%3A"))));
    }

    @Test
    public void ctrlHoverUsesOnlyTheWordUnderThePointer() {
        String source = "😀 <a href=\"{{ Model.Url }}\">{{ Model.Price.ToString(\"0.00\") }}</a> {% model Product %}";
        for (String word : new String[] { "Model", "Url", "Price", "ToString", "Product" }) {
            int offset = source.indexOf(word) + 1;
            var lexer = new SavannaParserDefinition().createLexer(null);
            lexer.start(source);
            while (lexer.getTokenEnd() <= offset) {
                lexer.advance();
            }
            var range = new com.intellij.openapi.util.TextRange(lexer.getTokenStart(), lexer.getTokenEnd());
            assertEquals(word, range.substring(source));
            var element = (com.intellij.psi.PsiElement) Proxy.newProxyInstance(PsiFile.class.getClassLoader(), new Class<?>[] { com.intellij.psi.PsiElement.class },
                (proxy, method, arguments) -> switch (method.getName()) {
                    case "isPhysical" -> true;
                    case "getTextOffset" -> range.getStartOffset();
                    case "getTextRange" -> range;
                    default -> null;
                });
            assertEquals(java.util.List.of(range), com.intellij.codeInsight.navigation.CtrlMouseDataKt.getReferenceRanges(element));
        }
    }

    @Test
    public void psiLexerRestartsAtEveryBoundaryWithoutSplittingUnicode() {
        String source = "😀 {{ Model.@class + Model.Δείγμα }}\n<input value=\"{{ Model.Url }}\">";
        var lexer = new SavannaPsiLexer();
        lexer.start(source);
        int end = 0;
        while (lexer.getTokenType() != null) {
            assertEquals(end, lexer.getTokenStart());
            var restarted = new SavannaPsiLexer();
            restarted.start(source, lexer.getTokenStart(), source.length(), lexer.getState());
            assertEquals(lexer.getTokenType(), restarted.getTokenType());
            assertEquals(lexer.getTokenEnd(), restarted.getTokenEnd());
            end = lexer.getTokenEnd();
            lexer.advance();
        }
        assertEquals(source.length(), end);
    }

    @Test
    public void testSemanticHighlightingIsEnabledForSavannaPsi() {
        var file = (PsiFile) Proxy.newProxyInstance(PsiFile.class.getClassLoader(), new Class<?>[] { PsiFile.class },
            (proxy, method, arguments) -> method.getName().equals("getLanguage") ? SavannaLanguage.INSTANCE : null);
        assertTrue(new SavannaLspCustomization().getSemanticTokensCustomizer().shouldAskServerForSemanticTokens(file));
    }
}
