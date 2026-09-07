import { Route, Routes } from "react-router-dom";
import Home from "./pages/Home";
import Upload from "./pages/Upload";
import Processing from "./pages/Processing";
import Results from "./pages/Results";
import { SessionProvider } from "./services/SessionContext";

export default function App() {
  return (
    <SessionProvider>
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/upload" element={<Upload />} />
        <Route path="/processing" element={<Processing />} />
        <Route path="/results" element={<Results />} />
      </Routes>
    </SessionProvider>
  );
}
