import "vite/modulepreload-polyfill";
import "./site.css";
import logoUrl from "./logo.svg";

const counter = document.querySelector("#counter");
const detailsButton = document.querySelector("#load-details");
let clicks = import.meta.hot?.data.clicks ?? 0;

// The backend lookup and this import share the same image in both development and production.
document.querySelector("#logo").src = logoUrl;

function updateCounter() {
    counter.textContent = `Clicked ${clicks} ${clicks === 1 ? "time" : "times"}`;
}

function incrementCounter() {
    clicks += 1;
    updateCounter();
}

async function loadDetails() {
    detailsButton.disabled = true;
    try {
        // Vite loads this chunk and its stylesheet only when the button is used.
        const {showDetails} = await import("./details.js");
        showDetails(document.querySelector("#details"));
    } catch {
        document.querySelector("#details").textContent = "That module could not be loaded. Please try again.";
        detailsButton.disabled = false;
    }
}

updateCounter();
counter.addEventListener("click", incrementCounter);
detailsButton.addEventListener("click", loadDetails);

if (import.meta.hot) {
    import.meta.hot.accept();
    import.meta.hot.dispose((data) => {
        // Keep the counter state, and let the replacement module attach its own listeners.
        data.clicks = clicks;
        counter.removeEventListener("click", incrementCounter);
        detailsButton.removeEventListener("click", loadDetails);
    });
}
