package dev.serval.savanna;

import com.intellij.openapi.application.ApplicationManager;
import com.intellij.openapi.components.PersistentStateComponent;
import com.intellij.openapi.components.Service;
import com.intellij.openapi.components.State;
import com.intellij.openapi.components.Storage;
import org.jetbrains.annotations.NotNull;

@Service(Service.Level.APP)
@State(name = "ServalSavannaSettings", storages = @Storage("serval-savanna.xml"))
public final class SavannaSettings implements PersistentStateComponent<SavannaSettings.Options> {
    private Options options = new Options();

    @Override
    public @NotNull Options getState() {
        return options;
    }

    @Override
    public void loadState(@NotNull Options state) {
        options = state;
    }

    public String getDotnetPath() {
        return options.dotnetPath == null || options.dotnetPath.isBlank() ? "dotnet" : options.dotnetPath;
    }

    public static SavannaSettings getInstance() {
        return ApplicationManager.getApplication().getService(SavannaSettings.class);
    }

    public static final class Options {
        public String dotnetPath = "dotnet";
    }
}
