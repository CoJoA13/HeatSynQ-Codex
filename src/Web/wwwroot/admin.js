(() => {
    if (window.heatSynQAdminWired) {
        return;
    }

    window.heatSynQAdminWired = true;

    document.addEventListener("click", event => {
        const mfaButton = event.target.closest("[data-mfa-begin]");
        if (mfaButton) {
            beginMfaSetup(mfaButton);
            return;
        }

        if (event.target.closest("[data-page-reload]")) {
            window.location.reload();
            return;
        }

        const openButton = event.target.closest("[data-dialog-open]");
        if (openButton) {
            document.getElementById(openButton.dataset.dialogOpen)?.showModal();
            return;
        }

        const closeButton = event.target.closest("[data-dialog-close]");
        if (closeButton) {
            closeButton.closest("dialog")?.close();
        }
    });

    document.addEventListener("submit", async event => {
        const form = event.target.closest("[data-api-form]");
        if (!form) {
            return;
        }

        event.preventDefault();
        const submitButton = form.querySelector("[type=submit]");
        const errorPanel = form.querySelector(".form-error");
        submitButton.disabled = true;
        errorPanel.hidden = true;
        errorPanel.textContent = "";

        const formData = new FormData(form);
        const payloadBuilders = {
            "create-user": () => ({
                Username: formData.get("Username"),
                Email: formData.get("Email"),
                DisplayName: formData.get("DisplayName"),
                Password: formData.get("Password"),
                RoleNames: formData.getAll("RoleNames"),
                Reason: formData.get("Reason")
            }),
            "create-role": () => ({
                Name: formData.get("Name"),
                Description: formData.get("Description"),
                PermissionKeys: formData.getAll("PermissionKeys"),
                Reason: formData.get("Reason")
            }),
            "update-role": () => ({
                PermissionKeys: formData.getAll("PermissionKeys"),
                Reason: formData.get("Reason")
            }),
            "change-user-status": () => ({
                IsEnabled: formData.get("IsEnabled") === "true",
                Reason: formData.get("Reason")
            }),
            "reset-user-password": () => ({
                TemporaryPassword: formData.get("TemporaryPassword"),
                Reason: formData.get("Reason")
            }),
            "revoke-user-sessions": () => ({
                Reason: formData.get("Reason")
            }),
            "create-permission-override": () => {
                const expiration = formData.get("ExpiresAt");
                return {
                    PermissionKey: formData.get("PermissionKey"),
                    Effect: formData.get("Effect"),
                    ExpiresAt: expiration ? new Date(expiration).toISOString() : null,
                    Reason: formData.get("Reason")
                };
            },
            "revoke-permission-override": () => ({
                Reason: formData.get("Reason")
            }),
            "enable-mfa": () => ({
                Code: formData.get("Code")
            }),
            "disable-mfa": () => ({
                CurrentPassword: formData.get("CurrentPassword")
            }),
            "facility-settings": () => ({
                CompanyName: formData.get("CompanyName"),
                FacilityName: formData.get("FacilityName"),
                FacilityCode: formData.get("FacilityCode"),
                TimeZoneId: formData.get("TimeZoneId"),
                DefaultRetentionYears: Number(formData.get("DefaultRetentionYears")),
                Version: formData.get("Version") || null,
                Reason: formData.get("Reason")
            }),
            "number-sequence": () => ({
                Prefix: formData.get("Prefix"),
                NextValue: Number(formData.get("NextValue")),
                Padding: Number(formData.get("Padding")),
                Version: formData.get("Version") || null,
                Reason: formData.get("Reason")
            }),
            "retention-policy": () => ({
                RetentionYears: Number(formData.get("RetentionYears")),
                Version: formData.get("Version") || null,
                Reason: formData.get("Reason")
            }),
            "legal-hold": () => ({
                Category: formData.get("Category"),
                EntityType: formData.get("EntityType"),
                EntityId: formData.get("EntityId"),
                Reason: formData.get("Reason")
            }),
            "release-legal-hold": () => ({
                Reason: formData.get("Reason")
            })
        };
        const buildPayload = payloadBuilders[form.dataset.apiForm];
        if (!buildPayload) {
            errorPanel.textContent = "This administration form is not configured.";
            errorPanel.hidden = false;
            submitButton.disabled = false;
            return;
        }

        try {
            const response = await fetch(form.action, {
                method: form.dataset.apiMethod || "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(buildPayload())
            });

            if (response.ok) {
                if (form.dataset.apiForm === "enable-mfa") {
                    const result = await response.json();
                    const panel = document.querySelector("[data-recovery-codes]");
                    const list = panel.querySelector("ol");
                    list.replaceChildren(...result.recoveryCodes.map(code => {
                        const item = document.createElement("li");
                        item.textContent = code;
                        return item;
                    }));
                    panel.hidden = false;
                    form.closest("[data-mfa-setup]").hidden = true;
                    return;
                }

                window.location.reload();
                return;
            }

            const problem = await response.json().catch(() => ({}));
            const fieldErrors = Object.values(problem.errors ?? {}).flat();
            errorPanel.textContent = fieldErrors.join(" ")
                || problem.error
                || problem.title
                || "The user could not be created.";
            errorPanel.hidden = false;
        } catch {
            errorPanel.textContent = "HeatSynQ could not reach the server. Your entries are still in the form.";
            errorPanel.hidden = false;
        } finally {
            submitButton.disabled = false;
        }
    });

    async function beginMfaSetup(button) {
        button.disabled = true;
        try {
            const response = await fetch("/api/v1/auth/mfa/authenticator", {
                method: "POST",
                credentials: "same-origin"
            });
            if (!response.ok) {
                throw new Error("Enrollment could not start.");
            }

            const enrollment = await response.json();
            const setup = document.querySelector("[data-mfa-setup]");
            setup.querySelector("[data-mfa-key]").textContent = enrollment.sharedKey;
            const authenticatorLink = setup.querySelector("[data-mfa-uri]");
            authenticatorLink.href = enrollment.authenticatorUri;
            setup.hidden = false;
        } catch {
            button.insertAdjacentText("afterend", " HeatSynQ could not start MFA enrollment.");
        } finally {
            button.disabled = false;
        }
    }
})();
