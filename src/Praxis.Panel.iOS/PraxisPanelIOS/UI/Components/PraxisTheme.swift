import UIKit

enum PraxisTheme {
    static let background = UIColor(red: 0.031, green: 0.039, blue: 0.055, alpha: 1)
    static let surface = UIColor(red: 0.073, green: 0.090, blue: 0.118, alpha: 1)
    static let surfaceRaised = UIColor(red: 0.092, green: 0.112, blue: 0.145, alpha: 1)
    static let line = UIColor.white.withAlphaComponent(0.10)
    static let lineBright = UIColor.white.withAlphaComponent(0.18)
    static let text = UIColor(red: 0.96, green: 0.97, blue: 0.99, alpha: 1)
    static let muted = UIColor(red: 0.51, green: 0.56, blue: 0.63, alpha: 1)
    static let dim = UIColor(red: 0.34, green: 0.39, blue: 0.46, alpha: 1)
    static let systemBlue = UIColor(red: 0.039, green: 0.518, blue: 1, alpha: 1)
    static let danger = UIColor(red: 1, green: 0.42, blue: 0.47, alpha: 1)
    static let green = UIColor(red: 0.34, green: 0.84, blue: 0.55, alpha: 1)

    static func accent(named name: String) -> UIColor {
        switch name.lowercased() {
        case "green": return UIColor(red: 0.40, green: 0.91, blue: 0.59, alpha: 1)
        case "cyan": return UIColor(red: 0.30, green: 0.81, blue: 0.93, alpha: 1)
        case "violet": return UIColor(red: 0.69, green: 0.53, blue: 1, alpha: 1)
        case "amber": return UIColor(red: 0.95, green: 0.72, blue: 0.33, alpha: 1)
        case "slate": return UIColor(red: 0.62, green: 0.67, blue: 0.75, alpha: 1)
        default: return UIColor(red: 0.36, green: 0.61, blue: 1, alpha: 1)
        }
    }

    static func configureCard(_ view: UIView, radius: CGFloat = 14) {
        view.backgroundColor = surface
        view.layer.cornerRadius = radius
        view.layer.borderWidth = 1
        view.layer.borderColor = line.cgColor
    }
}

extension UIView {
    func pinEdges(to view: UIView, insets: UIEdgeInsets = .zero) {
        translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: insets.left),
            trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -insets.right),
            topAnchor.constraint(equalTo: view.topAnchor, constant: insets.top),
            bottomAnchor.constraint(equalTo: view.bottomAnchor, constant: -insets.bottom)
        ])
    }
}
