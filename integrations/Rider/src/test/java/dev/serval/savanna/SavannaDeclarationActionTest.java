package dev.serval.savanna;

import com.intellij.codeInsight.navigation.actions.GotoDeclarationHandler;
import com.intellij.mock.MockProjectEx;
import com.intellij.openapi.editor.Editor;
import com.intellij.openapi.editor.impl.DocumentImpl;
import com.intellij.openapi.fileEditor.FileDocumentManager;
import com.intellij.openapi.project.Project;
import com.intellij.openapi.util.Disposer;
import com.intellij.openapi.vfs.VirtualFile;
import org.eclipse.lsp4j.services.LanguageServer;
import com.intellij.platform.lsp.api.LspClient;
import com.intellij.platform.lsp.api.LspClientDescriptor;
import com.intellij.psi.PsiFile;
import com.intellij.testFramework.ApplicationRule;
import com.intellij.testFramework.LightVirtualFile;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicInteger;
import kotlin.jvm.functions.Function1;
import org.eclipse.lsp4j.DefinitionParams;
import org.eclipse.lsp4j.Location;
import org.eclipse.lsp4j.LocationLink;
import org.eclipse.lsp4j.Position;
import org.eclipse.lsp4j.Range;
import org.eclipse.lsp4j.TextDocumentIdentifier;
import org.eclipse.lsp4j.jsonrpc.messages.Either;
import org.eclipse.lsp4j.services.TextDocumentService;
import org.junit.ClassRule;
import org.junit.Test;
import static org.junit.Assert.*;

public final class SavannaDeclarationActionTest {
    @ClassRule
    public static final ApplicationRule APPLICATION = new ApplicationRule();

    @Test
    public void riderHasNoBackendPolicyForSavannaDeclarations() throws Exception {
        var sourceFile = new LightVirtualFile("Card.svn", SavannaFileType.INSTANCE, "{{ Model.Url }}");
        var psi = psiFile(null, sourceFile);
        // RiderDefaultBackend defaults to the frontend when no language policy handles the action.
        // Reflect only in the test to keep the plugin independent of Rider's internal routing API.
        var support = Class.forName("com.jetbrains.rider.actions.RiderActionsSupport");
        var extensions = (com.intellij.lang.LanguageExtension<?>) support.getMethod("getExtensions").invoke(support.getField("INSTANCE").get(null));
        var policyType = Class.forName("com.jetbrains.rider.actions.RiderActionSupportPolicy");
        for (var policy : extensions.allForLanguageOrAny(SavannaLanguage.INSTANCE)) {
            assertNotEquals("A Rider backend policy must not intercept Savanna declarations", Boolean.TRUE,
                policyType.getMethod("isAvailable", com.intellij.psi.PsiElement.class, String.class).invoke(policy, psi, "GotoDeclaration"));
        }
    }

    @Test
    public void declarationActionHasAnExplicitSavannaHandler() {
        assertTrue(GotoDeclarationHandler.EP_NAME.getExtensionList().stream().anyMatch(handler -> handler instanceof SavannaGotoDeclarationHandler));
        assertTrue(com.intellij.codeInsight.highlighting.CodeBlockSupportHandler.EP.allForLanguage(SavannaLanguage.INSTANCE).stream()
                .anyMatch(handler -> handler instanceof SavannaCodeBlockSupportHandler));
    }

    @Test
    public void declarationActionResolvesLocationsAndLinksAtTheClickedOffset() throws Exception {
        var disposable = Disposer.newDisposable();
        try {
            var project = new MockProjectEx(disposable);
            var path = Files.createTempDirectory("savanna-declaration").resolve("Models With Space.cs");
            Files.writeString(path, "// model\npublic class Product { public string Url { get; } }\n");
            var sourceFile = new LightVirtualFile("Card.svn", SavannaFileType.INSTANCE, "{% model Product %}\n😀 <a href=\"{{ Model.Url }}\">");
            var source = psiFile(project, sourceFile);
            var sourceDocument = new DocumentImpl(sourceFile.getContent());
            var offset = sourceDocument.getText().indexOf("Url") + 1;
            var editor = proxy(Editor.class, (instance, method, arguments) -> method.getName().equals("getDocument") ? sourceDocument : null);
            var range = new Range(new Position(1, 37), new Position(1, 40));
            var requests = new AtomicInteger();
            var descriptor = new LspClientDescriptor(project, "test") {
                @Override
                public boolean isSupportedFile(VirtualFile file) { return file == sourceFile; }
                @Override
                public VirtualFile findFileByUri(String uri) { return SavannaLspIntegration.findLocalFile(java.net.URI.create(uri)); }
            };

            for (boolean links : new boolean[] { false, true }) {
                var textService = proxy(TextDocumentService.class, (instance, method, arguments) -> {
                    assertEquals("definition", method.getName());
                    var parameters = (DefinitionParams) arguments[0];
                    assertEquals("file:///Card.svn", parameters.getTextDocument().getUri());
                    assertEquals(new Position(1, offset - sourceDocument.getLineStartOffset(1)), parameters.getPosition());
                    requests.incrementAndGet();
                    var reply = links ? Either.forRight(List.of(new LocationLink(path.toUri().toString(), range, range)))
                        : Either.forLeft(List.of(new Location(path.toUri().toString(), range)));
                    return CompletableFuture.completedFuture(reply);
                });
                var server = proxy(LanguageServer.class, (instance, method, arguments) -> method.getName().equals("getTextDocumentService") ? textService : null);
                var client = proxy(LspClient.class, (instance, method, arguments) -> switch (method.getName()) {
                    case "getDescriptor" -> descriptor;
                    case "getDocumentIdentifier" -> new TextDocumentIdentifier("file:///Card.svn");
                    case "sendRequestSync" -> ((Function1<LanguageServer, CompletableFuture<?>>) arguments[1]).invoke(server).join();
                    default -> null;
                });
                var handler = new SavannaGotoDeclarationHandler(ignored -> List.of(client), SavannaDeclarationActionTest::psiFile);
                com.intellij.openapi.application.ReadAction.run(() -> {
                    var targets = handler.getGotoDeclarationTargets(source, offset, editor);
                    assertNotNull(targets);
                    assertEquals(1, targets.length);
                    var targetFile = targets[0].getContainingFile().getVirtualFile();
                    assertTrue("The declaration must resolve to the requested file", Files.isSameFile(path, targetFile.toNioPath()));
                    assertEquals(FileDocumentManager.getInstance().getDocument(targetFile).getLineStartOffset(1) + 37, targets[0].getTextOffset());
                    assertTrue(((com.intellij.pom.Navigatable) targets[0]).canNavigateToSource());
                });
            }

            assertEquals(2, requests.get());
        } finally {
            Disposer.dispose(disposable);
        }
    }

    private static PsiFile psiFile(Project project, VirtualFile file) {
        return proxy(PsiFile.class, (instance, method, arguments) -> switch (method.getName()) {
            case "getProject" -> project;
            case "getLanguage" -> SavannaLanguage.INSTANCE;
            case "getVirtualFile" -> file;
            case "getContainingFile" -> instance;
            case "isValid" -> true;
            default -> null;
        });
    }

    private static <T> T proxy(Class<T> type, java.lang.reflect.InvocationHandler handler) {
        return type.cast(Proxy.newProxyInstance(type.getClassLoader(), new Class<?>[] { type }, handler));
    }
}
