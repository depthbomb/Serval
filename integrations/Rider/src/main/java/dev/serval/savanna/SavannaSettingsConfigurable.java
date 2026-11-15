package dev.serval.savanna;

import com.intellij.openapi.options.Configurable;
import com.intellij.ui.components.JBLabel;
import com.intellij.ui.components.JBTextField;
import com.intellij.util.ui.FormBuilder;
import javax.swing.JComponent;
import org.jetbrains.annotations.Nls;

public final class SavannaSettingsConfigurable implements Configurable {
    private JBTextField dotnetPath;

    @Override
    public @Nls String getDisplayName() {
        return "Serval.Savanna";
    }

    @Override
    public JComponent createComponent() {
        dotnetPath = new JBTextField(SavannaSettings.getInstance().getDotnetPath());
        return FormBuilder.createFormBuilder()
            .addLabeledComponent(".NET executable:", dotnetPath)
            .addComponent(new JBLabel("Requires the .NET 11 SDK. Restart the Savanna language service after changing this path."))
            .getPanel();
    }

    @Override
    public boolean isModified() {
        return dotnetPath != null && !dotnetPath.getText().trim().equals(SavannaSettings.getInstance().getDotnetPath());
    }

    @Override
    public void apply() {
        SavannaSettings.getInstance().getState().dotnetPath = dotnetPath.getText().trim();
    }

    @Override
    public void reset() {
        if (dotnetPath != null) {
            dotnetPath.setText(SavannaSettings.getInstance().getDotnetPath());
        }
    }

    @Override
    public void disposeUIResources() {
        dotnetPath = null;
    }
}
