import "./details.css";

export function showDetails(element) {
    element.classList.add("details-loaded");
    element.textContent = "This module and its CSS were loaded on demand. The generated entry kept them out of the eager asset lists.";
}
